using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Media;

namespace Clowd.Drawing.History
{
    /// <summary>
    /// history.json encode/decode (persistent undo, MIGRATION.md §8.8). The document is
    /// <c>{ version, baseline, cursor, steps }</c>: <c>baseline</c> is the ROOT node's document in
    /// the exact graphics.json shape, <c>steps</c> are the delta chain root→tail with each field
    /// record encoded like a graphics.json array element (same GraphicFieldMap slots, same
    /// converters — one field contract), and <c>cursor</c> is how many steps separate the root
    /// from the current node. The cursor may sit mid-list, which is what lets redo survive a
    /// session reopen. graphics.json remains the authority: the loader replays baseline→cursor
    /// and the caller rejects the file unless the replayed document matches it.
    /// </summary>
    internal static class HistorySerializer
    {
        public const int Version = 1;

        /// <summary>Decoded history.json: the unlinked step list plus the record-space baseline
        /// the validation replay starts from.</summary>
        public sealed class ParsedHistory
        {
            public List<HistoryStep> Steps;
            public int Cursor;
            public Dictionary<string, FieldRecord> BaselineById;
            public string[] BaselineOrder;
            public Color BaselineBackground;
        }

        /// <summary>The record-space document at the saved cursor (validation replay result).</summary>
        public sealed class ReplayedState
        {
            public Dictionary<string, FieldRecord> ById;
            public string[] Order;
            public Color Background;
        }

        public static JsonObject Serialize(HistoryStep current, CommittedState committed)
        {
            var root = current;
            int cursor = 0;
            while (root.Previous != null)
            {
                root = root.Previous;
                cursor++;
            }

            var steps = new List<HistoryStep>();
            for (var n = root.Next; n != null; n = n.Next)
                steps.Add(n);

            // this runs on EVERY discrete history action (append/undo/redo raise StateUpdated
            // immediately), so the emission is assembled from cached per-step/baseline trees:
            // a plain undo/redo re-serializes nothing — it clones cached nodes and stamps a new
            // cursor. JsonNodes are single-parent, so the caches hand out DeepClones.
            root.CachedBaselineJson ??= BuildBaseline(steps, cursor, committed);

            var stepsJson = new JsonArray();
            foreach (var s in steps)
                stepsJson.Add((s.CachedJson ??= SerializeStep(s)).DeepClone());

            return new JsonObject
            {
                ["version"] = Version,
                ["baseline"] = root.CachedBaselineJson.DeepClone(),
                ["cursor"] = cursor,
                ["steps"] = stepsJson,
            };
        }

        /// <summary>
        /// The root node's document: the committed shadow (the state at the cursor) folded back
        /// through the Before sides of every step at or below the cursor. Records are complete
        /// captures and are never mutated, so the fold is pure reference bookkeeping.
        /// </summary>
        private static JsonObject BuildBaseline(List<HistoryStep> steps, int cursor, CommittedState committed)
        {
            var byId = new Dictionary<string, FieldRecord>(committed.ById, StringComparer.Ordinal);
            var order = committed.Order;
            var background = committed.Background;
            for (int j = cursor - 1; j >= 0; j--)
            {
                var s = steps[j];
                foreach (var delta in s.Graphics)
                {
                    if (delta.Before == null)
                        byId.Remove(delta.Id);
                    else
                        byId[delta.Id] = delta.Before;
                }

                if (s.Order.HasValue)
                    order = s.Order.Value.Before;
                if (s.Background.HasValue)
                    background = s.Background.Value.Before;
            }

            return SerializeState(byId, order, background);
        }

        /// <summary>
        /// Parses and structurally validates a history.json document. Throws on ANY malformation
        /// (unknown version, shape errors, unknown $type, record/delta id mismatches, duplicate
        /// baseline ids, out-of-range cursor, over-cap step count) — the caller treats every
        /// throw as "corrupt file" and falls back to empty history.
        /// </summary>
        public static ParsedHistory Deserialize(JsonObject history, int maxSteps)
        {
            if (history["version"]?.GetValue<int>() != Version)
                throw new JsonException("unsupported history version");

            var parsed = new ParsedHistory();

            var baseline = history["baseline"].AsObject();
            parsed.BaselineBackground = baseline["BackgroundColor"].Deserialize<Color>(GraphicsSerializer.Options);
            parsed.BaselineById = new Dictionary<string, FieldRecord>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var node in baseline["Graphics"].AsArray())
            {
                var record = DeserializeRecord(node.AsObject());
                var id = IdOf(record);
                parsed.BaselineById.Add(id, record); // Add: a duplicate baseline id is corrupt
                order.Add(id);
            }

            parsed.BaselineOrder = order.ToArray();

            parsed.Steps = new List<HistoryStep>();
            foreach (var node in history["steps"].AsArray())
                parsed.Steps.Add(DeserializeStep(node.AsObject()));
            if (parsed.Steps.Count > maxSteps)
                throw new JsonException("history exceeds the step cap");

            parsed.Cursor = history["cursor"].GetValue<int>();
            if (parsed.Cursor < 0 || parsed.Cursor > parsed.Steps.Count)
                throw new JsonException("history cursor out of range");

            return parsed;
        }

        /// <summary>
        /// Replays the parsed chain in record space and returns the document at the cursor (for
        /// the equality check against the real graphics.json). The whole chain — including the
        /// redo branch past the cursor — is folded, so the membership invariants (a step may
        /// only add an absent graphic, or edit/remove a present one) are enforced over every
        /// step even though only the cursor state is compared.
        /// </summary>
        public static ReplayedState ReplayToCursor(ParsedHistory parsed)
        {
            var byId = new Dictionary<string, FieldRecord>(parsed.BaselineById, StringComparer.Ordinal);
            var order = parsed.BaselineOrder;
            var background = parsed.BaselineBackground;
            ReplayedState atCursor = null;

            if (parsed.Cursor == 0)
                atCursor = Snapshot(byId, order, background);

            for (int j = 0; j < parsed.Steps.Count; j++)
            {
                var s = parsed.Steps[j];
                foreach (var delta in s.Graphics)
                {
                    if ((delta.Before == null) == byId.ContainsKey(delta.Id))
                        throw new JsonException("history step membership mismatch");

                    if (delta.After == null)
                        byId.Remove(delta.Id);
                    else
                        byId[delta.Id] = delta.After;
                }

                if (s.Order.HasValue)
                    order = s.Order.Value.After;
                if (s.Background.HasValue)
                    background = s.Background.Value.After;

                if (j == parsed.Cursor - 1)
                    atCursor = Snapshot(byId, order, background);
            }

            return atCursor;

            static ReplayedState Snapshot(Dictionary<string, FieldRecord> byId, string[] order, Color background) =>
                new ReplayedState
                {
                    ById = new Dictionary<string, FieldRecord>(byId, StringComparer.Ordinal),
                    Order = order,
                    Background = background,
                };
        }

        /// <summary>
        /// Serializes a replayed cursor state into the graphics.json shape. With
        /// <paramref name="normalize"/> the records are first materialized into instances and
        /// <c>Normalize()</c>d — the same treatment RestoreState gives graphics.json — so a
        /// document that drifted off its raw records only through Normalize's non-idempotent
        /// derived fields (a text's stale CenterOfRotation, rotated-rect ulps) compares equal to
        /// the equally-normalized live document.
        /// </summary>
        public static JsonObject SerializeReplayed(ReplayedState state, bool normalize)
        {
            if (!normalize)
                return SerializeState(state.ById, state.Order, state.Background);

            if (state.Order.Length != state.ById.Count)
                throw new JsonException("history order sequence does not match membership");

            var graphics = new JsonArray();
            foreach (var id in state.Order)
            {
                var record = state.ById[id];
                var inst = (Graphics.GraphicBase)record.Map.CreateObject();
                var slots = record.Map.Slots;
                for (int i = 0; i < slots.Length; i++)
                    slots[i].Set(inst, slots[i].Codec.Capture(record.Values[i]));
                inst.Normalize();
                graphics.Add(JsonSerializer.SerializeToNode(inst, typeof(Graphics.GraphicBase), GraphicsSerializer.Options));
            }

            return new JsonObject
            {
                ["BackgroundColor"] = JsonSerializer.SerializeToNode(state.Background, GraphicsSerializer.Options),
                ["Graphics"] = graphics,
            };
        }

        private static JsonObject SerializeStep(HistoryStep step)
        {
            var changes = new JsonArray();
            foreach (var c in step.Changes)
                changes.Add((JsonNode)c);

            var graphics = new JsonArray();
            foreach (var d in step.Graphics)
            {
                graphics.Add(new JsonObject
                {
                    ["id"] = d.Id,
                    ["index"] = d.Index,
                    ["before"] = d.Before == null ? null : SerializeRecord(d.Before),
                    ["after"] = d.After == null ? null : SerializeRecord(d.After),
                });
            }

            var json = new JsonObject
            {
                ["changes"] = changes,
                ["graphics"] = graphics,
            };

            if (step.Order.HasValue)
            {
                json["order"] = new JsonObject
                {
                    ["before"] = ToIdArray(step.Order.Value.Before),
                    ["after"] = ToIdArray(step.Order.Value.After),
                };
            }

            if (step.Background.HasValue)
            {
                json["background"] = new JsonObject
                {
                    ["before"] = JsonSerializer.SerializeToNode(step.Background.Value.Before, GraphicsSerializer.Options),
                    ["after"] = JsonSerializer.SerializeToNode(step.Background.Value.After, GraphicsSerializer.Options),
                };
            }

            return json;
        }

        private static HistoryStep DeserializeStep(JsonObject json)
        {
            var changes = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var c in json["changes"].AsArray())
                changes.Add(c.GetValue<string>());
            if (changes.Count == 0)
                throw new JsonException("history step with no changes"); // only the root is change-less

            var deltas = new List<GraphicDelta>();
            foreach (var node in json["graphics"].AsArray())
            {
                var obj = node.AsObject();
                var delta = new GraphicDelta(obj["id"].GetValue<string>())
                {
                    Index = obj["index"]?.GetValue<int>() ?? -1,
                };
                if (obj["before"] is JsonObject before)
                    delta.Before = DeserializeRecord(before);
                if (obj["after"] is JsonObject after)
                    delta.After = DeserializeRecord(after);
                if (delta.Before == null && delta.After == null)
                    throw new JsonException("history delta with no sides");
                if ((delta.Before != null && IdOf(delta.Before) != delta.Id) ||
                    (delta.After != null && IdOf(delta.After) != delta.Id))
                    throw new JsonException("history record id mismatch");
                deltas.Add(delta);
            }

            var step = new HistoryStep { Changes = changes, Graphics = deltas.ToArray() };

            if (json["order"] is JsonObject orderJson)
                step.Order = (ToIds(orderJson["before"]), ToIds(orderJson["after"]));
            if (json["background"] is JsonObject backgroundJson)
                step.Background = (backgroundJson["before"].Deserialize<Color>(GraphicsSerializer.Options),
                                   backgroundJson["after"].Deserialize<Color>(GraphicsSerializer.Options));

            return step;
        }

        /// <summary>A record serializes exactly like a graphics.json array element: "$type" first
        /// (STJ's polymorphic reader requires the discriminator up front), then one property per
        /// persisted field slot through the shared converters.</summary>
        private static JsonObject SerializeRecord(FieldRecord record)
        {
            var json = new JsonObject { ["$type"] = record.Map.TypeName };
            var slots = record.Map.Slots;
            for (int i = 0; i < slots.Length; i++)
                json[slots[i].JsonName] = JsonSerializer.SerializeToNode(record.Values[i], slots[i].FieldType, GraphicsSerializer.Options);
            return json;
        }

        private static FieldRecord DeserializeRecord(JsonObject json)
        {
            // decode through the serializer (same $type resolution, converters and
            // absent-field-keeps-ctor-default semantics as graphics.json); the materialized
            // graphic is only a decode vehicle. Rehydrated records carry a NULL Instance:
            // live-instance retention cannot survive the process, and a foreign non-null
            // instance would push ApplyRecord's in-place check into remove+reinsert (wrong
            // z-index for plain field edits). Undo-of-delete instead reconstructs from the
            // captured fields via the map's CreateObject.
            var graphic = json.Deserialize<Graphics.GraphicBase>(GraphicsSerializer.Options)
                          ?? throw new JsonException("null graphic record");
            var map = GraphicFieldMap.For(graphic.GetType());
            return new FieldRecord(map, map.Capture(graphic), null);
        }

        private static string IdOf(FieldRecord record)
        {
            var slots = record.Map.Slots;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].JsonName == "id")
                    return (string)record.Values[i];
            }

            throw new JsonException("graphic record has no id slot");
        }

        private static JsonObject SerializeState(Dictionary<string, FieldRecord> byId, string[] order, Color background)
        {
            if (order.Length != byId.Count)
                throw new JsonException("history order sequence does not match membership");

            var graphics = new JsonArray();
            foreach (var id in order)
                graphics.Add(SerializeRecord(byId[id])); // a missing id is corrupt → KeyNotFound → fallback

            return new JsonObject
            {
                ["BackgroundColor"] = JsonSerializer.SerializeToNode(background, GraphicsSerializer.Options),
                ["Graphics"] = graphics,
            };
        }

        private static string[] ToIds(JsonNode node)
        {
            var arr = node.AsArray();
            var ids = new string[arr.Count];
            for (int i = 0; i < arr.Count; i++)
                ids[i] = arr[i].GetValue<string>();
            return ids;
        }

        private static JsonArray ToIdArray(string[] ids)
        {
            var arr = new JsonArray();
            foreach (var id in ids)
                arr.Add((JsonNode)id);
            return arr;
        }
    }
}
