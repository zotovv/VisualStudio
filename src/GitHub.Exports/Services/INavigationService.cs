using System.Collections.Generic;

namespace GitHub.Services
{
    public interface INavigationService
    {
        /// <summary>
        /// Find the closest matching line in <see cref="toLines"/>.
        /// </summary>
        /// <param name="fromLines">The document we're navigating from.</param>
        /// <param name="toLines">The document we're navigating to.</param>
        /// <param name="line">The 0-based line we're navigating from.</param>
        /// <returns>The best matching line in <see cref="toLines"/></returns>
        int FindMatchingLine(IList<string> fromLines, IList<string> toLines, int line, int matchLinesAbove = 0);
    }
}