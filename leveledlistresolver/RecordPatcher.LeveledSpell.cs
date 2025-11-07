using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;
using Noggog;

namespace leveledlistresolver
{
    sealed partial class RecordPatcher : IRecordPatcher<ILeveledSpellGetter, LeveledSpell>
    {
        static readonly LeveledSpell.TranslationMask LvSpMask = new(true)
        {
            FormVersion = false,
            VersionControl = false,
            Version2 = false,
            Entries = false,
        };

        static readonly LeveledSpell.TranslationMask LvSpMask2 = new(false)
        {
            ChanceNone = true,
            Flags = true,
        };

        bool IRecordPatcher<ILeveledSpellGetter, LeveledSpell>.Try(
            IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
            FormKey formKey,
            out LeveledSpell? setter
        )
        {
            setter = default;

            var extentContexts = state.LinkCache.GetExtentContexts<ILeveledSpellGetter>(formKey);
            if (extentContexts.Length < 2)
            {
                if (extentContexts.Length == 0)
                    return false;

                var winning = extentContexts[0].Record;
                if (winning.Entries?.Any(static i => i.IsNullEntry()) ?? false)
                {
                    setter = state.PatchMod.LeveledSpells.GetOrAddAsOverride(winning);
                    if (Program.Settings.VerboseLogging)
                        Console.WriteLine(
                            $"Removed {setter.Entries!.RemoveAll(Utility.IsNullEntry)} null entries from {setter.EditorID} [{formKey}]{Environment.NewLine}"
                        );
                    else
                        setter.Entries!.RemoveAll(Utility.IsNullEntry);
                    return true;
                }

                return false;
            }

            var highest = extentContexts[0].Record;
            var lowest = state.LinkCache.GetLowestOverride<ILeveledSpellGetter>(formKey);

            bool hasConflict = false;
            foreach (var (_, record) in extentContexts[1..])
            {
                if (
                    !record.Equals(lowest, LvSpMask)
                    || !Utility.UnsortedEqual(record.Entries, lowest.Entries)
                )
                {
                    hasConflict = true;
                    break;
                }
            }

            if (!hasConflict)
            {
                if (Program.Settings.VerboseLogging)
                    Console.WriteLine(
                        $"Skipped {highest.EditorID} [{formKey}] - no conflict detected\n"
                    );
                return false;
            }

            var copy = state.PatchMod.LeveledSpells.GetOrAddAsOverride(highest);
            copy.FormVersion = 44;
            copy.VersionControl = Utility.Timestamp;
            copy.Entries = [];

            bool hasEditorIdConflict = !string.Equals(
                lowest.EditorID,
                copy.EditorID,
                StringComparison.InvariantCulture
            );
            bool hasPropertiesConflict = !copy.Equals(lowest, LvSpMask2);

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

                if (!hasPropertiesConflict && !lowest.Equals(record, LvSpMask2))
                {
                    copy.ChanceNone = record.ChanceNone;
                    copy.Flags = record.Flags;
                    hasPropertiesConflict = true;
                }
            }

            List<ILeveledSpellEntryGetter> entries = [];
            if (
                lowest.Entries is { Count: > 0 }
                && extentContexts.All(static i => i.Record.Entries is { Count: > 0 })
            )
            {
                var e = (IEnumerable<ILeveledSpellEntryGetter>)lowest.Entries!;
                var intersection = extentContexts.Aggregate(
                    e,
                    (i, k) => i.IntersectExt(k.Record.Entries)
                );
                entries.AddRange(intersection);
            }

            var disjunction = extentContexts.Aggregate(
                Enumerable.Empty<ILeveledSpellEntryGetter>(),
                (i, k) =>
                    i.Concat(
                        k.Record.Entries?.DisjunctLeft(lowest.Entries).DisjunctLeft(i)
                            ?? Enumerable.Empty<ILeveledSpellEntryGetter>()
                    )
            );
            entries.AddRange(disjunction);

            if (Program.Settings.RemoveEmptySublists)
                entries.RemoveAll(i => i.IsNullOrEmptySublist(state.LinkCache));
            else
                entries.RemoveAll(Utility.IsNullEntry);
            entries.Sort(static (i, k) => (i.Data?.Level ?? 0).CompareTo(k.Data?.Level));

            if (entries.Count > 255)
            {
                int i = 1;
                foreach (var chunk in entries.Chunk(255))
                {
                    LeveledSpell lvsp = new(state.PatchMod, $"{copy.EditorID}Chunk_{i}")
                    {
                        FormVersion = 44,
                        VersionControl = Utility.Timestamp,
                        ChanceNone = copy.ChanceNone,
                        Flags = copy.Flags,
                        Entries = chunk.Select(static i => i.DeepCopy()).ToExtendedList(),
                    };

                    state.PatchMod.LeveledSpells.Add(lvsp);

                    LeveledSpellEntry entry = new()
                    {
                        Data = new()
                        {
                            Level = 1,
                            Reference = lvsp.ToLink(),
                            Count = 1,
                        },
                    };

                    copy.Entries.Add(entry);
                    i++;
                }
            }
            else
            {
                copy.Entries.AddRange(entries.Select(static i => i.DeepCopy()));
            }

            if (Program.Settings.VerboseLogging)
            {
                var modKeys = state
                    .LoadOrder.ListedOrder.Where(i =>
                        i.Mod?.LeveledSpells.ContainsKey(formKey) ?? false
                    )
                    .Select(static i => i.ModKey)
                    .ToHashSet();

                Console.WriteLine($"{copy.EditorID} [{formKey}]");
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

            if (
                copy.Equals(highest, LvSpMask)
                && Utility.UnsortedEqual(copy.Entries, highest.Entries)
            )
            {
                if (Program.Settings.VerboseLogging)
                    Console.WriteLine($"Skipped {copy.EditorID}\n");
                return false;
            }

            if (Program.Settings.VerboseLogging)
                Console.WriteLine();
            setter = copy;
            return true;
        }
    }
}
