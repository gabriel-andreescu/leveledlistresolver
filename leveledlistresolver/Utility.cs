using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;
using Noggog;

namespace leveledlistresolver
{
    internal static class Utility
    {
        public static uint Timestamp { get; } =
            (uint)(
                Math.Max(1, DateTime.Today.Year - 2000) << 9
                | DateTime.Today.Month << 5
                | DateTime.Today.Day
            );

        internal static IEnumerable<T> IntersectExt<T>(
            this IEnumerable<T> source,
            IEnumerable<T>? other,
            IEqualityComparer<T>? comparer = null
        )
            where T : class
        {
            var _comparer = comparer ?? EqualityComparer<T>.Default;

            if (!source.Any() || other == null || !other.Any())
                yield break;

            HashSet<T> set = new(source, _comparer);
            set.IntersectWith(other);
            Dictionary<T, int> dict = new(_comparer);

            foreach (var it in other)
            {
                if (!set.Contains(it))
                    continue;

                if (!dict.TryAdd(it, 1))
                    dict[it]++;
            }

            foreach (var it in source)
            {
                if (dict.TryGetValue(it, out int count) && count > 0)
                {
                    count--;
                    dict[it] = count;
                    if (count <= 0)
                        dict.Remove(it);
                    yield return it;
                }
            }
        }

        internal static bool UnsortedEqual<T>(
            this IReadOnlyList<T>? first,
            IReadOnlyList<T>? second
        )
            where T : class
        {
            if (first is null)
                return second is null;

            if (second is null)
                return false;

            if (first.Count != second.Count)
                return false;

            Dictionary<T, int> dictionary = [];

            foreach (var it in first)
            {
                if (!dictionary.TryAdd(it, 1))
                    dictionary[it]++;
            }

            foreach (var it in second)
            {
                if (!dictionary.TryGetValue(it, out var count) || count is 0)
                    return false;
                dictionary[it]--;
            }

            return dictionary.Values.All(static i => i is 0);
        }

        public static IEnumerable<T> DisjunctLeft<T>(
            this IEnumerable<T> left,
            IEnumerable<T>? right,
            IEqualityComparer<T>? comparer = null
        )
            where T : notnull
        {
            if (right == null || !right.Any())
            {
                foreach (var it in left)
                    yield return it;
                yield break;
            }

            Dictionary<T, int> dict = new(comparer ?? EqualityComparer<T>.Default);

            foreach (var it in right)
            {
                if (!dict.TryAdd(it, 1))
                    dict[it]++;
            }

            foreach (var it in left)
            {
                if (dict.TryGetValue(it, out int count) && count > 0)
                {
                    dict[it]--;
                    continue;
                }

                yield return it;
            }
        }

        internal static bool IsNullEntry(this ILeveledItemEntryGetter entry)
        {
            return entry is { Data: null or { Reference.IsNull: true } };
        }

        internal static bool IsNullEntry(this ILeveledNpcEntryGetter entry)
        {
            return entry is { Data: null or { Reference.IsNull: true } };
        }

        internal static bool IsNullEntry(this ILeveledSpellEntryGetter entry)
        {
            return entry is { Data: null or { Reference.IsNull: true } };
        }

        internal static bool IsNullOrEmptySublist(
            this ILeveledItemEntryGetter entry,
            ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache
        )
        {
            if (entry is { Data: null or { Reference.IsNull: true } })
                return true;
            return entry.Data.Reference.TryResolve<ILeveledItemGetter>(linkCache)
                is { Entries: null or { Count: 0 } };
        }

        internal static bool IsNullOrEmptySublist(
            this ILeveledNpcEntryGetter entry,
            ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache
        )
        {
            if (entry is { Data: null or { Reference.IsNull: true } })
                return true;
            return entry.Data.Reference.TryResolve<ILeveledNpcGetter>(linkCache)
                is { Entries: null or { Count: 0 } };
        }

        internal static bool IsNullOrEmptySublist(
            this ILeveledSpellEntryGetter entry,
            ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache
        )
        {
            if (entry is { Data: null or { Reference.IsNull: true } })
                return true;
            return entry.Data.Reference.TryResolve<ILeveledSpellGetter>(linkCache)
                is { Entries: null or { Count: 0 } };
        }

        internal static TGet GetLowestOverride<TGet>(
            this ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
            FormKey formKey
        )
            where TGet : class, IMajorRecordGetter
        {
            var contexts = linkCache.ResolveAllSimpleContexts<TGet>(formKey).ToArray();

            if (contexts.Length == 0)
                throw new InvalidOperationException($"No contexts found for FormKey {formKey}");

            var origin = contexts[^1].Record;

            if (contexts.Length <= 2)
            {
                return origin;
            }

            var keys = Array.ConvertAll(contexts, static i => i.ModKey).ToHashSet();
            foreach (var ctx in contexts)
            {
                var masters =
                    linkCache
                        .PriorityOrder.FirstOrDefault(i => i.ModKey == ctx.ModKey)
                        ?.MasterReferences.Select(static i => i.Master)
                    ?? Enumerable.Empty<ModKey>();
                keys.IntersectWith(masters);
            }

            if (keys.Count > 0)
                return contexts.FirstOrDefault(i => keys.Contains(i.ModKey))?.Record ?? origin;

            return origin;
        }

        /// <summary>
        /// Returns mod contexts that should be merged based on master dependency relationships.
        /// Filters out mods that don't actually depend on each other to prevent false conflicts.
        /// </summary>
        /// <remarks>
        /// This method determines which mod overrides represent actual conflicts that need merging.
        /// When multiple mods override the same record, they only conflict if they share a dependency chain.
        /// This prevents merging unrelated mods that happen to modify the same record independently.
        ///
        /// Algorithm: For each mod context, collect its master references and intersect with the set
        /// of mods that override this record. Only include contexts that reference other overriding mods.
        /// </remarks>
        internal static IModContext<TGet>[] GetExtentContexts<TGet>(
            this ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
            FormKey formKey
        )
            where TGet : class, IMajorRecordGetter
        {
            var contexts = linkCache.ResolveAllSimpleContexts<TGet>(formKey).ToArray();

            if (contexts.Length <= 2)
            {
                return contexts.Length > 0 ? [contexts[0]] : [];
            }

            HashSet<ModKey> dependencyKeys = [];
            var overridingModKeys = Array.ConvertAll(contexts, i => i.ModKey);
            List<IModContext<TGet>> result = [];

            foreach (var ctx in contexts[..^1])
            {
                if (!dependencyKeys.Contains(ctx.ModKey))
                {
                    var index = linkCache.ListedOrder.IndexOf(
                        ctx.ModKey,
                        static (i, k) => i.ModKey == k
                    );
                    if (index >= 0)
                    {
                        dependencyKeys.UnionWith(
                            linkCache
                                .ListedOrder[index]
                                .MasterReferences.Select(static i => i.Master)
                            ?? Enumerable.Empty<ModKey>()
                        );
                        dependencyKeys.IntersectWith(overridingModKeys);
                    }
                    result.Add(ctx);
                }
            }

            return [.. result];
        }

        internal static bool HasConflict<TGet, TEntry>(
            IModContext<TGet>[] extentContexts,
            TGet lowest,
            Func<TGet, TGet, bool> equalsWithMask,
            Func<TGet, IReadOnlyList<TEntry>?> getEntries
        )
            where TGet : class, IMajorRecordGetter
            where TEntry : class
        {
            foreach (var (_, record) in extentContexts[1..])
            {
                if (
                    !equalsWithMask(record, lowest)
                    || !UnsortedEqual(getEntries(record), getEntries(lowest))
                )
                {
                    return true;
                }
            }
            return false;
        }

        internal static void ResolveEditorIdConflict<TGet>(
            IModContext<TGet>[] extentContexts,
            TGet lowest,
            IMajorRecord copy,
            ref bool hasEditorIdConflict
        )
            where TGet : class, IMajorRecordGetter
        {
            if (string.IsNullOrWhiteSpace(copy.EditorID))
            {
                copy.EditorID = Guid.NewGuid().ToString("n");
                hasEditorIdConflict = true;
            }

            foreach (var (_, record) in extentContexts[1..])
            {
                if (
                    !hasEditorIdConflict
                    && !string.Equals(
                        lowest.EditorID,
                        record.EditorID,
                        StringComparison.InvariantCulture
                    )
                )
                {
                    copy.EditorID = record.EditorID;
                    hasEditorIdConflict = true;
                }
            }
        }

        internal static void ResolvePropertyConflicts<TGet>(
            IModContext<TGet>[] extentContexts,
            TGet lowest,
            Func<TGet, TGet, bool> equalsWithMask,
            ref bool hasPropertiesConflict,
            Action<TGet> applyProperties
        )
            where TGet : class, IMajorRecordGetter
        {
            foreach (var (_, record) in extentContexts[1..])
            {
                if (!hasPropertiesConflict && !equalsWithMask(lowest, record))
                {
                    applyProperties(record);
                    hasPropertiesConflict = true;
                }
            }
        }

        internal static List<TEntry> MergeEntries<TGet, TEntry>(
            IModContext<TGet>[] extentContexts,
            TGet lowest,
            Func<TGet, IReadOnlyList<TEntry>?> getEntries
        )
            where TGet : class, IMajorRecordGetter
            where TEntry : class
        {
            List<TEntry> entries = [];

            if (
                getEntries(lowest) is { Count: > 0 }
                && extentContexts.All(i => getEntries(i.Record) is { Count: > 0 })
            )
            {
                var e = (IEnumerable<TEntry>)getEntries(lowest)!;
                var intersection = extentContexts.Aggregate(
                    e,
                    (i, k) => i.IntersectExt(getEntries(k.Record))
                );
                entries.AddRange(intersection);
            }

            var disjunction = extentContexts.Aggregate(
                Enumerable.Empty<TEntry>(),
                (i, k) =>
                    i.Concat(
                        getEntries(k.Record)?.DisjunctLeft(getEntries(lowest)).DisjunctLeft(i)
                            ?? Enumerable.Empty<TEntry>()
                    )
            );
            entries.AddRange(disjunction);

            return entries;
        }

        internal static void LogVerboseMergeInfo<TGet>(
            IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
            IModContext<TGet>[] extentContexts,
            FormKey formKey,
            string? editorId,
            Func<IModListingGetter<ISkyrimModGetter>, bool> containsKey
        )
            where TGet : class, IMajorRecordGetter
        {
            if (!Program.Settings.VerboseLogging)
                return;

            var modKeys = state
                .LoadOrder.ListedOrder.Where(containsKey)
                .Select(static i => i.ModKey)
                .ToHashSet();

            Console.WriteLine($"{editorId} [{formKey}]");
            foreach (var ctx in extentContexts.Reverse())
            {
                if (!state.LoadOrder.ContainsKey(ctx.ModKey))
                    continue;

                var masters = state
                    .LoadOrder[ctx.ModKey]
                    .Mod?.MasterReferences.Select(static i => i.Master)
                    .Where(modKeys.Contains);
                if (masters != null)
                    Console.WriteLine($"{string.Join(" -> ", masters)} -> {ctx.ModKey}");
            }
        }

        internal static bool ShouldSkipUnchanged<TGet, TEntry>(
            TGet highest,
            IMajorRecordGetter copy,
            Func<IMajorRecordGetter, TGet, bool> equalsWithMask,
            Func<TGet, IReadOnlyList<TEntry>?> getHighestEntries,
            Func<IMajorRecordGetter, IReadOnlyList<TEntry>?> getCopyEntries,
            string? editorId
        )
            where TGet : class, IMajorRecordGetter
            where TEntry : class
        {
            if (
                equalsWithMask(copy, highest)
                && UnsortedEqual(getCopyEntries(copy), getHighestEntries(highest))
            )
            {
                if (Program.Settings.VerboseLogging)
                    Console.WriteLine($"Skipped {editorId}\n");
                return true;
            }
            return false;
        }

        internal static void ProcessLeveledListEntries<TEntry>(
            List<TEntry> entries,
            ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
            Func<TEntry, bool> isNullEntry,
            Func<TEntry, ILinkCache<ISkyrimMod, ISkyrimModGetter>, bool> isNullOrEmptySublist,
            Func<TEntry, int> getLevel
        )
            where TEntry : class
        {
            if (Program.Settings.RemoveEmptySublists)
                entries.RemoveAll(new Predicate<TEntry>(e => isNullOrEmptySublist(e, linkCache)));
            else
                entries.RemoveAll(new Predicate<TEntry>(isNullEntry));
            entries.Sort((i, k) => getLevel(i).CompareTo(getLevel(k)));
        }

        internal static void Deconstruct<TGet>(
            this IModContext<TGet> modContext,
            out ModKey modKey,
            out TGet record
        )
            where TGet : class, IMajorRecordGetter
        {
            modKey = modContext.ModKey;
            record = modContext.Record;
        }

        internal static void Deconstruct<TMod, TModGetter, TSet, TGet>(
            this IModContext<TMod, TModGetter, TSet, TGet> modContext,
            out ModKey modKey,
            out TGet record
        )
            where TModGetter : class, IModGetter
            where TMod : class, IMod, TModGetter
            where TGet : class, IMajorRecordGetter
            where TSet : class, IMajorRecord, TGet
        {
            modKey = modContext.ModKey;
            record = modContext.Record;
        }
    }
}
