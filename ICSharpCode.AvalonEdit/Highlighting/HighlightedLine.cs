﻿// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Utils;
using ICSharpCode.NRefactory.Editor;

namespace ICSharpCode.AvalonEdit.Highlighting
{
	/// <summary>
	/// Represents a highlighted document line.
	/// </summary>
	public class HighlightedLine
	{
		/// <summary>
		/// Creates a new HighlightedLine instance.
		/// </summary>
		public HighlightedLine(IDocument document, IDocumentLine documentLine)
		{
			if (document == null)
				throw new ArgumentNullException("document");
			//if (!document.Lines.Contains(documentLine))
			//	throw new ArgumentException("Line is null or not part of document");
			this.Document = document;
			this.DocumentLine = documentLine;
			this.Sections = new NullSafeCollection<HighlightedSection>();
		}
		
		/// <summary>
		/// Gets the document associated with this HighlightedLine.
		/// </summary>
		public IDocument Document { get; private set; }
		
		/// <summary>
		/// Gets the document line associated with this HighlightedLine.
		/// </summary>
		public IDocumentLine DocumentLine { get; private set; }
		
		/// <summary>
		/// Gets the highlighted sections.
		/// The sections are not overlapping, but they may be nested.
		/// In that case, outer sections come in the list before inner sections.
		/// The sections are sorted by start offset.
		/// </summary>
		public IList<HighlightedSection> Sections { get; private set; }
		
		[Conditional("DEBUG")]
		void ValidateInvariants()
		{
			var line = this;
			int lineStartOffset = line.DocumentLine.Offset;
			int lineEndOffset = line.DocumentLine.EndOffset;
			for (int i = 0; i < line.Sections.Count; i++) {
				HighlightedSection s1 = line.Sections[i];
				if (s1.Offset < lineStartOffset || s1.Length < 0 || s1.Offset + s1.Length > lineEndOffset)
					throw new InvalidOperationException("Section is outside line bounds");
				for (int j = i + 1; j < line.Sections.Count; j++) {
					HighlightedSection s2 = line.Sections[j];
					if (s2.Offset >= s1.Offset + s1.Length) {
						// s2 is after s1
					} else if (s2.Offset >= s1.Offset && s2.Offset + s2.Length <= s1.Offset + s1.Length) {
						// s2 is nested within s1
					} else {
						throw new InvalidOperationException("Sections are overlapping or incorrectly sorted.");
					}
				}
			}
		}
		
		#region Merge
		/// <summary>
		/// Merges the additional line into this line.
		/// </summary>
		public void MergeWith(HighlightedLine additionalLine)
		{
			if (additionalLine == null)
				return;
			ValidateInvariants();
			additionalLine.ValidateInvariants();
			
			int pos = 0;
			Stack<int> activeSectionEndOffsets = new Stack<int>();
			int lineEndOffset = this.DocumentLine.EndOffset;
			activeSectionEndOffsets.Push(lineEndOffset);
			foreach (HighlightedSection newSection in additionalLine.Sections) {
				int newSectionStart = newSection.Offset;
				// Track the existing sections using the stack, up to the point where
				// we need to insert the first part of the newSection
				while (pos < this.Sections.Count) {
					HighlightedSection s = this.Sections[pos];
					if (newSection.Offset < s.Offset)
						break;
					while (s.Offset > activeSectionEndOffsets.Peek()) {
						activeSectionEndOffsets.Pop();
					}
					activeSectionEndOffsets.Push(s.Offset + s.Length);
					pos++;
				}
				// Now insert the new section
				// Create a copy of the stack so that we can track the sections we traverse
				// during the insertion process:
				Stack<int> insertionStack = new Stack<int>(activeSectionEndOffsets.Reverse());
				// The stack enumerator reverses the order of the elements, so we call Reverse() to restore
				// the original order.
				int i;
				for (i = pos; i < this.Sections.Count; i++) {
					HighlightedSection s = this.Sections[i];
					if (newSection.Offset + newSection.Length <= s.Offset)
						break;
					// Insert a segment in front of s:
					Insert(ref i, ref newSectionStart, s.Offset, newSection.Color, insertionStack);
					
					while (s.Offset > insertionStack.Peek()) {
						insertionStack.Pop();
					}
					insertionStack.Push(s.Offset + s.Length);
				}
				Insert(ref i, ref newSectionStart, newSection.Offset + newSection.Length, newSection.Color, insertionStack);
			}
			
			ValidateInvariants();
		}
		
		void Insert(ref int pos, ref int newSectionStart, int insertionEndPos, HighlightingColor color, Stack<int> insertionStack)
		{
			if (newSectionStart >= insertionEndPos) {
				// nothing to insert here
				return;
			}
			
			while (insertionStack.Peek() <= newSectionStart) {
				insertionStack.Pop();
			}
			while (insertionStack.Peek() < insertionEndPos) {
				int end = insertionStack.Pop();
				// insert the portion from newSectionStart to end
				if (end > newSectionStart) {
					this.Sections.Insert(pos++, new HighlightedSection {
					                     	Offset = newSectionStart,
					                     	Length = end - newSectionStart,
					                     	Color = color
					                     });
					newSectionStart = end;
				}
			}
			if (insertionEndPos > newSectionStart) {
				this.Sections.Insert(pos++, new HighlightedSection {
				                     	Offset = newSectionStart,
				                     	Length = insertionEndPos - newSectionStart,
				                     	Color = color
				                     });
				newSectionStart = insertionEndPos;
			}
		}
		#endregion
		
		#region ToHtml
		sealed class HtmlElement : IComparable<HtmlElement>
		{
			internal readonly int Offset;
			internal readonly int Nesting;
			internal readonly bool IsEnd;
			internal readonly HighlightingColor Color;
			
			public HtmlElement(int offset, int nesting, bool isEnd, HighlightingColor color)
			{
				this.Offset = offset;
				this.Nesting = nesting;
				this.IsEnd = isEnd;
				this.Color = color;
			}
			
			public int CompareTo(HtmlElement other)
			{
				int r = Offset.CompareTo(other.Offset);
				if (r != 0)
					return r;
				if (IsEnd != other.IsEnd) {
					if (IsEnd)
						return -1;
					else
						return 1;
				} else {
					if (IsEnd)
						return other.Nesting.CompareTo(Nesting);
					else
						return Nesting.CompareTo(other.Nesting);
				}
			}
		}
		
		/// <summary>
		/// Produces HTML code for the line, with &lt;span class="colorName"&gt; tags.
		/// </summary>
		public string ToHtml(HtmlOptions options)
		{
			int startOffset = this.DocumentLine.Offset;
			return ToHtml(startOffset, startOffset + this.DocumentLine.Length, options);
		}
		
		/// <summary>
		/// Produces HTML code for a section of the line, with &lt;span class="colorName"&gt; tags.
		/// </summary>
		public string ToHtml(int startOffset, int endOffset, HtmlOptions options)
		{
			if (options == null)
				throw new ArgumentNullException("options");
			int documentLineStartOffset = this.DocumentLine.Offset;
			int documentLineEndOffset = documentLineStartOffset + this.DocumentLine.Length;
			if (startOffset < documentLineStartOffset || startOffset > documentLineEndOffset)
				throw new ArgumentOutOfRangeException("startOffset", startOffset, "Value must be between " + documentLineStartOffset + " and " + documentLineEndOffset);
			if (endOffset < startOffset || endOffset > documentLineEndOffset)
				throw new ArgumentOutOfRangeException("endOffset", endOffset, "Value must be between startOffset and " + documentLineEndOffset);
			ISegment requestedSegment = new SimpleSegment(startOffset, endOffset - startOffset);
			
			List<HtmlElement> elements = new List<HtmlElement>();
			for (int i = 0; i < this.Sections.Count; i++) {
				HighlightedSection s = this.Sections[i];
				if (SimpleSegment.GetOverlap(s, requestedSegment).Length > 0) {
					elements.Add(new HtmlElement(s.Offset, i, false, s.Color));
					elements.Add(new HtmlElement(s.Offset + s.Length, i, true, s.Color));
				}
			}
			elements.Sort();
			
			IDocument document = this.Document;
			StringWriter w = new StringWriter(CultureInfo.InvariantCulture);
			int textOffset = startOffset;
			foreach (HtmlElement e in elements) {
				int newOffset = Math.Min(e.Offset, endOffset);
				if (newOffset > startOffset) {
					HtmlClipboard.EscapeHtml(w, document.GetText(textOffset, newOffset - textOffset), options);
				}
				textOffset = Math.Max(textOffset, newOffset);
				if (options.ColorNeedsSpanForStyling(e.Color)) {
					if (e.IsEnd) {
						w.Write("</span>");
					} else {
						w.Write("<span");
						options.WriteStyleAttributeForColor(w, e.Color);
						w.Write('>');
					}
				}
			}
			HtmlClipboard.EscapeHtml(w, document.GetText(textOffset, endOffset - textOffset), options);
			return w.ToString();
		}
		
		/// <inheritdoc/>
		public override string ToString()
		{
			return "[" + GetType().Name + " " + ToHtml(new HtmlOptions()) + "]";
		}
		#endregion
		
		/// <summary>
		/// Creates a <see cref="HighlightedInlineBuilder"/> that stores the text and highlighting of this line.
		/// </summary>
		public HighlightedInlineBuilder ToInlineBuilder()
		{
			HighlightedInlineBuilder builder = new HighlightedInlineBuilder(Document.GetText(DocumentLine));
			int startOffset = DocumentLine.Offset;
			// copy only the foreground and background colors
			foreach (HighlightedSection section in Sections) {
				if (section.Color.Foreground != null) {
					builder.SetForeground(section.Offset - startOffset, section.Length, section.Color.Foreground.GetBrush(null));
				}
				if (section.Color.Background != null) {
					builder.SetBackground(section.Offset - startOffset, section.Length, section.Color.Background.GetBrush(null));
				}
			}
			return builder;
		}
	}
}
