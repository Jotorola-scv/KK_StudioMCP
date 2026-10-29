using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace StudioMcp.Bridge
{
    internal sealed class BatchCommands
    {
        private readonly Plugin _plugin;
        private readonly Stack<Action> _undo = new Stack<Action>();
        private string _session, _id;
        internal BatchCommands(Plugin plugin) { _plugin = plugin; }
        internal void Clear() { _undo.Clear(); _id = null; }

        internal JObject Execute(string method, JObject args)
        {
            if (method == "batch.undo")
            {
                if (_id == null || _id != (string)args["batchId"] || _session != _plugin.Session)
                    throw new ArgumentException("Only the most recent batch in the same scene can be undone.");
                var errors = Undo();
                return new JObject { ["restored"] = errors.Count == 0, ["errors"] = errors };
            }
            if (method != "batch.apply") throw new ArgumentException("Unknown batch method.");
            var operations = args["operations"] as JArray;
            if (operations == null || operations.Count == 0 || operations.Count > 32)
                throw new ArgumentException("A batch requires 1-32 operations.");
            // Validate every operation and capture its undo before making any changes.
            var restore = new List<Action>();
            foreach (JObject operation in operations)
            {
                var op = (string)operation["method"];
                var values = operation["args"] as JObject ?? throw new ArgumentException("Operation args are required.");
                if (op == "me.set") restore.Add(_plugin.Materials.PrepareUndo(values));
                else if (op == "render.set")
                {
                    RenderCommands.Validate((JObject)values["values"]);
                    var before = _plugin.Rendering.State();
                    var ownedSettings = _plugin.Rendering.OwnsSettings;
                    restore.Add(() => {
                        _plugin.Rendering.Apply(before);
                        _plugin.Rendering.OwnsSettings = ownedSettings;
                    });
                }
                else if (op == "chara.shape_set") restore.Add(_plugin.Characters.PrepareUndo(values));
                else if (op == "chara.accessory_set") restore.Add(_plugin.Accessories.PrepareUndo(values));
                else if (op == "chara.bones_set") restore.Add(_plugin.Bones.PrepareUndo(values));
                else if (!Plugin.InStudio) restore.Add(_plugin.Maker.PrepareUndo(op, values));
                else restore.Add(_plugin.Scene.PrepareUndo(op, values));
            }
            Clear();
            _id = Guid.NewGuid().ToString("N");
            _session = _plugin.Session;
            var results = new JArray();
            try
            {
                for (var i = 0; i < operations.Count; i++)
                {
                    _undo.Push(restore[i]);
                    var operation = (JObject)operations[i];
                    results.Add(_plugin.Dispatch((string)operation["method"], (JObject)operation["args"]));
                }
                return new JObject { ["batchId"] = _id, ["results"] = results, ["undoAvailable"] = true };
            }
            catch (Exception error)
            {
                var failures = Undo();
                return new JObject { ["applied"] = false, ["error"] = error.GetBaseException().Message,
                    ["rolledBack"] = failures.Count == 0, ["rollbackErrors"] = failures };
            }
        }

        private JArray Undo()
        {
            var errors = new JArray();
            while (_undo.Count > 0)
                try { _undo.Pop()(); } catch (Exception error) { errors.Add(error.GetBaseException().Message); }
            _id = null;
            return errors;
        }
    }
}
