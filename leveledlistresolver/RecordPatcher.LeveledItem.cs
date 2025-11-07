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
    sealed partial class RecordPatcher : IRecordPatcher<ILeveledItemGetter, LeveledItem>
    {
        readonly LeveledItem.TranslationMask LvliMask = new(true)
        {
            FormVersion = false,
            VersionControl = false,
            Version2 = false,
            Entries = false,
        };

        readonly LeveledItem.TranslationMask LvliMask2 = new(false)
        {
            ChanceNone = true,
            Flags = true,
            Global = true,
        };

        bool IRecordPatcher<ILeveledItemGetter, LeveledItem>.Try(
            IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
            FormKey formKey,
            out LeveledItem? setter
        )
        {
            setter = default;

            var extentContexts = Program
                .LinkCache.GetExtentContexts<ILeveledItemGetter>(formKey)
                .ToArray();
            if (extentContexts.Length < 2)
            {
                if (extentContexts.Length == 0)
                    return false;
                
                var winning = extentContexts[0].Record;
                if (winning.Entries?.Any(static i => i.IsNullEntry()) ?? false)
                {
                    setter = winning.DeepCopy();
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
            var lowest = Program.LinkCache.GetLowestOverride<ILeveledItemGetter>(formKey);

            bool hasConflict = false;
            foreach (var (_, record) in extentContexts[1..])
            {
                if (
                    !record.Equals(lowest, LvliMask)
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

            var copy = highest.DeepCopy();
            copy.FormVersion = 44;
            copy.VersionControl = Utility.Timestamp;
            copy.Entries = [];

            bool a = !string.Equals(
                lowest.EditorID,
                copy.EditorID,
                StringComparison.InvariantCulture
            );
            bool b = !copy.Equals(lowest, LvliMask2);

            if (string.IsNullOrWhiteSpace(copy.EditorID))
            {
                copy.EditorID = Guid.NewGuid().ToString("n");
                a = true;
            }

            foreach (var (_, record) in extentContexts[1..])
            {
                if (!a && !string.Equals(lowest.EditorID, record.EditorID, StringComparison.InvariantCulture))
                {
                    copy.EditorID = record.EditorID;
                    a = true;
                }

                if (!b && !lowest.Equals(record, LvliMask2))
                {
                    copy.ChanceNone = record.ChanceNone;
                    copy.Flags = record.Flags;
                    copy.Global.SetTo(record.Global);
                    b = true;
                }
            }

            List<ILeveledItemEntryGetter> entries = [];
            if (
                lowest.Entries is { Count: > 0 }
                && extentContexts.All(static i => i.Record.Entries is { Count: > 0 })
            )
            {
                var e = (IEnumerable<ILeveledItemEntryGetter>)lowest.Entries!;
                var intersection = extentContexts.Aggregate(
                    e,
                    (i, k) => i.IntersectExt(k.Record.Entries)
                );
                entries.AddRange(intersection);
            }

            var disjunction = extentContexts.Aggregate(
                Enumerable.Empty<ILeveledItemEntryGetter>(),
                (i, k) =>
                    i.Concat(
                        k.Record.Entries?.DisjunctLeft(lowest.Entries).DisjunctLeft(i)
                            ?? Enumerable.Empty<ILeveledItemEntryGetter>()
                    )
            );
            entries.AddRange(disjunction);

            if (Program.Settings.RemoveEmptySublists)
                entries.RemoveAll(i => i.IsNullOrEmptySublist(Program.LinkCache));
            else
                entries.RemoveAll(Utility.IsNullEntry);
            entries.Sort(static (i, k) => (i.Data?.Level ?? 0).CompareTo(k.Data?.Level));

            if (entries.Count > 255)
            {
                int i = 1;
                foreach (var chunk in entries.Chunk(255))
                {
                    LeveledItem item = new(state.PatchMod, $"{copy.EditorID}Chunk_{i}")
                    {
                        FormVersion = 44,
                        VersionControl = Utility.Timestamp,
                        ChanceNone = copy.ChanceNone,
                        Flags = copy.Flags,
                        Global = copy.Global.AsNullable(),
                        Entries = chunk.Select(static i => i.DeepCopy()).ToExtendedList(),
                    };

                    state.PatchMod.LeveledItems.Add(item);

                    LeveledItemEntry entry = new()
                    {
                        Data = new()
                        {
                            Level = 1,
                            Reference = item.ToLink(),
                            Count = 1,
                        },
                    };

                    copy.Entries.Add(entry);
                    i++;
                }
            }
            else
            {
                copy.Entries.AddRange(entries.ConvertAll(static i => i.DeepCopy()));
            }

            if (Program.Settings.VerboseLogging)
            {
                var modKeys = state
                    .LoadOrder.ListedOrder.Where(i =>
                        i.Mod?.LeveledItems.ContainsKey(formKey) ?? false
                    )
                    .Select(static i => i.ModKey)
                    .ToHashSet();

                Console.WriteLine($"{copy.EditorID} [{formKey}]");
                foreach (var ctx in extentContexts.Reverse())
                {
                    var masters = state
                        .LoadOrder[ctx.ModKey]
                        .Mod?.MasterReferences.Select(static i => i.Master)
                        .Where(modKeys.Contains);
                    if (masters != null)
                        Console.WriteLine($"{string.Join(" -> ", masters)} -> {ctx.ModKey}");
                }
            }

            if (
                copy.Equals(highest, LvliMask)
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
