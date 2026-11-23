using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using BIMinFormation.Ids;

namespace BIMinFormation.Revit.Services
{
    /// <summary>
    /// Queue of requests from the modeless windows to Revit. Every call to the document goes through here: Revit runs the
    /// <see cref="ExternalEvent"/> when no command or edit mode is active.
    /// </summary>
    public sealed class RevitQueue : IExternalEventHandler
    {
        private readonly Queue<(string Name, Action<UIApplication> Action)> _pending = new Queue<(string, Action<UIApplication>)>();
        private readonly ExternalEvent _event;

        /// <summary>Must be created in a valid API context (inside a command).</summary>
        public RevitQueue()
        {
            _event = ExternalEvent.Create(this);
        }

        public void Enqueue(string name, Action<UIApplication> action)
        {
            lock (_pending)
            {
                _pending.Enqueue((name, action));
            }

            _event.Raise();
        }

        public void Execute(UIApplication application)
        {
            while (true)
            {
                (string Name, Action<UIApplication> Action) item;
                lock (_pending)
                {
                    if (_pending.Count == 0)
                    {
                        return;
                    }

                    item = _pending.Dequeue();
                }

                try
                {
                    item.Action(application);
                }
                catch (Exception ex)
                {
                    Log.Error("Failed: '" + item.Name + "'", ex);
                    TaskDialog.Show("BIMinFormation", Tr.T("Falha em ") + item.Name + ":\n" + ex.Message);
                }
            }
        }

        public string GetName() => "BIMinFormation";
    }
}
