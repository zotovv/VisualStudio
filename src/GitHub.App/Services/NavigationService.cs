using System.Collections.Generic;
using System.ComponentModel.Composition;

namespace GitHub.Services
{
    [Export(typeof(INavigationService))]
    public class NavigationService : INavigationService
    {
        /// <summary>
        /// Find the closest matching line in <see cref="toLines"/>.
        /// </summary>
        /// <remarks>
        /// When matching we prioritize unique matching lines in <see cref="toLines"/>. If the target line isn't
        /// unique, continue searching the lines above for a better match and use this as anchor with an offset.
        /// The closest match to <see cref="line"/> with the fewest duplicate matches will be used for the matching line.
        /// </remarks>
        /// <param name="fromLines">The document we're navigating from.</param>
        /// <param name="toLines">The document we're navigating to.</param>
        /// <param name="line">The 0-based line we're navigating from.</param>
        /// <returns>The best matching line in <see cref="toLines"/></returns>
        public int FindMatchingLine(IList<string> fromLines, IList<string> toLines, int line, int matchLinesAbove = 0)
        {
            var matchingLine = -1;
            var minMatchedLines = -1;
            for (var offset = 0; offset <= matchLinesAbove; offset++)
            {
                var targetLine = line - offset;
                if (targetLine < 0)
                {
                    break;
                }

                int matchedLines;
                var nearestLine = FindNearestMatchingLine(fromLines, toLines, targetLine, out matchedLines);
                if (nearestLine != -1)
                {
                    if (matchingLine == -1 || minMatchedLines >= matchedLines)
                    {
                        matchingLine = nearestLine + offset;
                        minMatchedLines = matchedLines;
                    }

                    if (minMatchedLines == 1)
                    {
                        break; // We've found a unique matching line!
                    }
                }
            }

            if (matchingLine >= toLines.Count)
            {
                matchingLine = toLines.Count - 1;
            }

            return matchingLine;
        }

        /// <summary>
        /// Find the nearest matching line to <see cref="line"/> and the number of similar matched lines in the text.
        /// </summary>
        /// <param name="fromLines">The document we're navigating from.</param>
        /// <param name="toLines">The document we're navigating to.</param>
        /// <param name="line">The 0-based line we're navigating from.</param>
        /// <param name="matchedLines">The number of similar matched lines in <see cref="toLines"/></param>
        /// <returns>Find the nearest matching line in <see cref="toLines"/>.</returns>
        public int FindNearestMatchingLine(IList<string> fromLines, IList<string> toLines, int line, out int matchedLines)
        {
            line = line < fromLines.Count ? line : fromLines.Count - 1; // VS shows one extra line at end
            var fromLine = fromLines[line];

            matchedLines = 0;
            var matchingLine = -1;
            for (var offset = 0; true; offset++)
            {
                var lineAbove = line + offset;
                var checkAbove = lineAbove < toLines.Count;
                if (checkAbove && toLines[lineAbove] == fromLine)
                {
                    if (matchedLines == 0)
                    {
                        matchingLine = lineAbove;
                    }

                    matchedLines++;
                }

                var lineBelow = line - offset;
                var checkBelow = lineBelow >= 0;
                if (checkBelow && offset > 0 && lineBelow < toLines.Count && toLines[lineBelow] == fromLine)
                {
                    if (matchedLines == 0)
                    {
                        matchingLine = lineBelow;
                    }

                    matchedLines++;
                }

                if (!checkAbove && !checkBelow)
                {
                    break;
                }
            }

            return matchingLine;
        }
    }
}
