using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;

namespace leveledlistresolver
{
    sealed partial class RecordPatcher : IRecordPatcher<IContainerGetter, Container>
    {
        static readonly Container.TranslationMask ContMask = new(true)
        {
            FormVersion = false,
            VersionControl = false,
            Items = false,
        };

        static readonly Container.TranslationMask ContMask2 = new(false) { Flags = true };

        bool IRecordPatcher<IContainerGetter, Container>.Try(
            IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
            FormKey formKey,
            out Container? setter
        )
        {
            setter = default;

            var extentContexts = Program
                .LinkCache.GetExtentContexts<IContainerGetter>(formKey)
                .ToArray();
            if (extentContexts.Length < 2)
            {
                return false;
            }

            var highest = extentContexts[0].Record;
            var lowest = Program.LinkCache.GetLowestOverride<IContainerGetter>(formKey);

            bool hasConflict = false;
            foreach (var (_, record) in extentContexts[1..])
            {
                if (
                    !record.Equals(lowest, ContMask)
                    || !Utility.UnsortedEqual(record.Items, lowest.Items)
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
            copy.Items = new();

            bool a = !string.Equals(
                lowest.EditorID,
                copy.EditorID,
                StringComparison.InvariantCulture
            );
            bool b = !copy.Equals(lowest, ContMask2);

            if (string.IsNullOrWhiteSpace(copy.EditorID))
            {
                copy.EditorID = Guid.NewGuid().ToString("n");
                a = true;
            }

            foreach (var (_, record) in extentContexts[1..])
            {
                if (
                    !a
                    && !string.Equals(
                        lowest.EditorID,
                        record.EditorID,
                        StringComparison.InvariantCulture
                    )
                )
                {
                    copy.EditorID = record.EditorID;
                    a = true;
                }

                if (!b && !lowest.Equals(record, ContMask2))
                {
                    copy.Flags = record.Flags;
                    b = true;
                }
            }

            List<IContainerEntryGetter> items = new();
            if (
                lowest.Items is { Count: > 0 }
                && extentContexts.All(static i => i.Record.Items is { Count: > 0 })
            )
            {
                var e = (IEnumerable<IContainerEntryGetter>)lowest.Items!;
                var intersection = extentContexts.Aggregate(
                    e,
                    (i, k) => i.IntersectExt(k.Record.Items)
                );
                items.AddRange(intersection);
            }

            var disjunction = extentContexts.Aggregate(
                Enumerable.Empty<IContainerEntryGetter>(),
                (i, k) =>
                    i.Concat(
                        k.Record.Items?.DisjunctLeft(lowest.Items).DisjunctLeft(i)
                            ?? Enumerable.Empty<IContainerEntryGetter>()
                    )
            );
            items.AddRange(disjunction);

            copy.Items.AddRange(items.ConvertAll(static i => i.DeepCopy()));

            if (Program.Settings.VerboseLogging)
            {
                var modKeys = state
                    .LoadOrder.ListedOrder.Where(i =>
                        i.Mod?.Containers.ContainsKey(formKey) ?? false
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

            if (copy.Equals(highest, ContMask) && Utility.UnsortedEqual(copy.Items, highest.Items))
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
