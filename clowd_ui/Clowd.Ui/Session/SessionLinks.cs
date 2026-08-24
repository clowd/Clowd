using System;
using System.Collections.Generic;

namespace Clowd
{
    /// <summary>
    /// Which sessions were made <i>from</i> which — a GIF from the video it was converted from, a
    /// render from the project it came out of. Two very different things read this: the Recent page,
    /// which lays a chain out as one block of rows and brackets them together, and the automatic
    /// cleanup, which must not delete half of a chain the user has starred. They agree only because
    /// they ask the same code, which is why this is not a private helper of either of them.
    /// </summary>
    public static class SessionLinks
    {
        /// <summary>
        /// The link graph over <paramref name="sessions"/>: for each entry the one it was made from,
        /// and the reverse index of everything made from a given entry. Only entries in
        /// <paramref name="sessions"/> can appear on either side — a source that is not in the list
        /// is simply not linked, which is what makes it safe to build this over a filtered list.
        /// </summary>
        /// <remarks>Sessions are matched by identity throughout, never by value, so both maps are
        /// keyed on reference equality (SessionInfo does not override Equals).</remarks>
        public static (Dictionary<SessionInfo, SessionInfo> Parents, Dictionary<SessionInfo, List<SessionInfo>> Children)
            BuildGraph(IReadOnlyList<SessionInfo> sessions)
        {
            // an entry's source is matched by path, so both maps prefer whichever of any duplicates
            // comes first — callers pass the list newest-first, which is the rule the Recent page's
            // own ordering follows.
            var byVideoPath = new Dictionary<string, SessionInfo>(StringComparer.OrdinalIgnoreCase);
            var byRenderKey = new Dictionary<string, SessionInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var session in sessions)
            {
                if (!String.IsNullOrEmpty(session.VideoPath))
                    byVideoPath.TryAdd(session.VideoPath, session);

                // a render output is never itself the source of a render, so it cannot be a parent
                // by this key — without that a re-render of an entry could point at itself.
                if (String.IsNullOrEmpty(session.EditSourceVideoPath) && !String.IsNullOrEmpty(session.RenderSourceKey))
                    byRenderKey.TryAdd(session.RenderSourceKey, session);
            }

            var parents = new Dictionary<SessionInfo, SessionInfo>();
            var children = new Dictionary<SessionInfo, List<SessionInfo>>();
            foreach (var session in sessions)
            {
                var parent = FindSource(session, byVideoPath, byRenderKey);
                if (parent == null || ReferenceEquals(parent, session))
                    continue;

                parents[session] = parent;
                if (!children.TryGetValue(parent, out var siblings))
                    children[parent] = siblings = new List<SessionInfo>();
                siblings.Add(session);
            }

            return (parents, children);
        }

        /// <summary>
        /// Every entry on a chain carrying at least one star. A star is put on the thing the user
        /// cares about — usually the finished GIF or render — but what it means is "keep this",
        /// and an entry whose source has been swept away is only half kept: the row it was made
        /// from is gone, the bracket joining them is gone, and re-rendering or re-converting it is
        /// no longer possible. So the star covers the whole chain, in both directions and however
        /// long it is, for the Recent page's "Starred" filter and for the retention rule alike.
        /// </summary>
        public static HashSet<SessionInfo> CollectStarredChains(IReadOnlyList<SessionInfo> sessions)
        {
            var (parents, children) = BuildGraph(sessions);

            var keep = new HashSet<SessionInfo>();
            var pending = new Queue<SessionInfo>();

            foreach (var session in sessions)
            {
                if (session.Starred && keep.Add(session))
                    pending.Enqueue(session);
            }

            // breadth-first over the undirected graph, so a star reaches the far end of a chain it
            // is in the middle of (project → render → GIF, starred on the render). The set doubles
            // as the visited mark, so a chain looping back on itself — two entries naming each
            // other as their source — terminates rather than spinning here.
            while (pending.Count > 0)
            {
                var session = pending.Dequeue();

                if (parents.TryGetValue(session, out var parent) && keep.Add(parent))
                    pending.Enqueue(parent);

                if (!children.TryGetValue(session, out var siblings))
                    continue;

                foreach (var child in siblings)
                {
                    if (keep.Add(child))
                        pending.Enqueue(child);
                }
            }

            return keep;
        }

        /// <summary>The entry <paramref name="session"/> was made from, when it is in the list too:
        /// the video a GIF was converted from, or the project a render came out of.</summary>
        private static SessionInfo FindSource(SessionInfo session,
            Dictionary<string, SessionInfo> byVideoPath, Dictionary<string, SessionInfo> byRenderKey)
        {
            if (!String.IsNullOrEmpty(session.SourceVideoPath))
                return byVideoPath.GetValueOrDefault(session.SourceVideoPath);

            if (!String.IsNullOrEmpty(session.EditSourceVideoPath))
                return byRenderKey.GetValueOrDefault(session.EditSourceVideoPath);

            return null;
        }
    }
}
