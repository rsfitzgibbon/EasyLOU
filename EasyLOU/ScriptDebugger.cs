﻿using LOU;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Debugging;
using MoonSharp.Interpreter.Loaders;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

namespace EasyLOU
{
    class ScriptDebugger : IDebugger
    {
        public enum DebuggerStatus
        {
            Idle = 0,
            Running = 2,
            Paused = 3,
            Stopped = 4
        }
        public DebuggerStatus Status;

        private MainForm MainForm;
        public String Guid { get; set; }
        public String Name { get; set; }
        private Thread ScriptThread;
        private Script Script;
        private List<DynamicExpression> m_Dynamics = new List<DynamicExpression>();

        public object varsLock = new object();
        public Dictionary<string, string> vars = new Dictionary<string, string>();

        public ScriptDebugger(MainForm MainForm, String Guid, String Name)
        {
            this.MainForm = MainForm;
            this.Guid = Guid;
            this.Name = Name;
        }

        void Sleep(int millisecondsTimeout)
        {
            System.Threading.Thread.Sleep(millisecondsTimeout);
        }

        void Print(String s)
        {
            this.MainForm.Invoke(new MainForm.PrintOutputDelegate(this.MainForm.PrintOutput), new object[] { this.Guid, s });
        }

        static DynValue CallBack(ScriptExecutionContext ctx, CallbackArguments args)
        {
            var name = ctx.m_Callback.Name;
            var arguments = args.GetArray();
            // do stuff

            ClientCommand Command = new ClientCommand((LOU.CommandType)Enum.Parse(typeof(LOU.CommandType), name));
            for (int i = 0; i < arguments.Length; i++)
            {
                switch (arguments[i].Type)
                {
                    case DataType.Number:
                        Command.CommandParams.Add(i.ToString(), arguments[i].Number.ToString());
                        break;
                    case DataType.String:
                        Command.CommandParams.Add(i.ToString(), arguments[i].String);
                        break;
                    default:
                        Command.CommandParams.Add(i.ToString(), arguments[i].ToString());
                        break;
                }
            }

            if (MainForm.CurrentClientProcessId != -1 && MainForm.ClientCommandsMemoryMap != null)
            {
                int ClientCommandId = 0;
                Queue<ClientCommand> ClientCommandsQueue;
                ClientCommand[] ClientCommandsArray;
                MainForm.ClientCommandsMemoryMap.ReadMemoryMap(out ClientCommandId, out ClientCommandsArray);
                if (ClientCommandsArray == null)
                {
                    ClientCommandsQueue = new Queue<ClientCommand>();
                }
                else
                {
                    ClientCommandsQueue = new Queue<ClientCommand>(ClientCommandsArray);
                }

                if (ClientCommandsQueue.Count > 100)
                {
                    throw new Exception("Too many commands in the queue. Cannot continue.");
                }

                ClientCommandsQueue.Enqueue(Command);
                int AssignedClientCommandId = ClientCommandId + ClientCommandsQueue.Count;
                MainForm.ClientCommandsMemoryMap.WriteMemoryMap(ClientCommandId, ClientCommandsQueue.ToArray());
                Debug.WriteLine("Command inserted, assigned CommandId=" + AssignedClientCommandId.ToString());

                Stopwatch timeout = new Stopwatch();
                timeout.Start();
                while (ClientCommandId < AssignedClientCommandId && timeout.ElapsedMilliseconds < 3000)
                {
                    Debug.WriteLine("Waiting for command to be executed, Current CommandId=" + ClientCommandId.ToString() + ", Assigned CommandId=" + AssignedClientCommandId.ToString());
                    Thread.Sleep(50);
                    MainForm.ClientCommandsMemoryMap.ReadMemoryMap(out ClientCommandId, out ClientCommandsArray);
                }
                timeout.Stop();
                if (timeout.ElapsedMilliseconds >= 3000) {
                    Debug.WriteLine("Timed out!");
                }
                MainForm.RefreshClientStatus();
            }

            return DynValue.Nil;
        }

        private static DynValue VarCallBack(Table table, DynValue index)
        {
            if (index.Type == DataType.String)
            {
                lock (MainForm.ClientStatusLock)
                {
                    string value;
                    if (MainForm.ClientStatus.CharacterInfo.TryGetValue(index.String, out value)) return DynValue.NewString(value);
                    else if (MainForm.ClientStatus.CharacterInfo.TryGetValue(index.String, out value)) return DynValue.NewString(value);
                    else if (MainForm.ClientStatus.StatusBar.TryGetValue(index.String, out value)) return DynValue.NewString(value);
                    else if (MainForm.ClientStatus.ContainerInfo.TryGetValue(index.String, out value)) return DynValue.NewString(value);
                    else if (MainForm.ClientStatus.LastAction.TryGetValue(index.String, out value)) return DynValue.NewString(value);
                    else if (MainForm.ClientStatus.Find.TryGetValue(index.String, out value)) return DynValue.NewString(value);
                    else if (MainForm.ClientStatus.ShopInfo.TryGetValue(index.String, out value)) return DynValue.NewString(value);
                    else if (MainForm.ClientStatus.ExtendedInfo.TryGetValue(index.String, out value)) return DynValue.NewString(value);
                    else if (MainForm.ClientStatus.ClientInfo.TryGetValue(index.String, out value)) return DynValue.NewString(value);
                    else if (MainForm.ClientStatus.CombatInfo.TryGetValue(index.String, out value)) return DynValue.NewString(value);
                    else if (MainForm.ClientStatus.TileInfo.TryGetValue(index.String, out value)) return DynValue.NewString(value);
                    else if (MainForm.ClientStatus.TimeInfo.TryGetValue(index.String, out value)) return DynValue.NewString(value);
                    else if (MainForm.ClientStatus.Miscellaneous.TryGetValue(index.String, out value)) return DynValue.NewString(value);
                }
            }
            return DynValue.Nil;
        }

        public void Play(String script, String path)
        {
            switch (this.Status)
            {
                case DebuggerStatus.Idle:
                case DebuggerStatus.Stopped:
                    {
                        if (this.ScriptThread != null)
                        {
                            try
                            {
                                this.ScriptThread.Abort();
                            }
                            finally
                            {
                                this.ScriptThread = null;
                            }
                        }

                        this.Status = DebuggerStatus.Running;
                        this.ScriptThread = new Thread((ThreadStart)delegate
                        {
                            try
                            {
                                this.Script = new Script();

                                ((ScriptLoaderBase)Script.Options.ScriptLoader).ModulePaths = new string[] { path + "\\?" };

                                // LOU commands
                                foreach (var s in Enum.GetValues(typeof(CommandType)))
                                {
                                    this.Script.Globals[s.ToString()] = DynValue.NewCallback(CallBack, s.ToString());
                                }

                                // LOU status variables
                                this.Script.Globals.MetaTable = new Table(this.Script);
                                this.Script.Globals.MetaTable["__index"] = (Func<Table, DynValue, DynValue>)VarCallBack;

                                // generic helper methods
                                this.Script.Globals["sleep"] = (Action<int>)Sleep;

                                // other options
                                this.Script.Options.DebugPrint = Print;

                                this.Script.AttachDebugger(this);

                                this.Script.DoString(code: script, codeFriendlyName: this.Name);
                            }
                            catch (System.Threading.ThreadAbortException)
                            {
                                // Do nothing, we're stopping the script
                            }
                            catch (SyntaxErrorException ex)
                            {
                                MessageBoxEx.Show(MainForm.TheMainForm, ex.DecoratedMessage);
                            }
                            catch (Exception ex)
                            {
                                MessageBoxEx.Show(MainForm.TheMainForm, ex.Message);
                            }
                            finally
                            {
                                this.Status = DebuggerStatus.Stopped;
                                this.MainForm.Invoke(new MainForm.RefreshToolStripStatusDelegate(this.MainForm.RefreshToolStripStatus));
                            }
                        })
                        {
                            Priority = ThreadPriority.BelowNormal,
                            IsBackground = true
                        };
                        this.ScriptThread.Start();
                    }
                    break;
                case DebuggerStatus.Paused:
                    {
                        this.Status = DebuggerStatus.Running;
                    }
                    break;
                case DebuggerStatus.Running:
                    {
                        // weird
                    }
                    break;
                default:
                    break;
            }
        }

        public void Pause()
        {
            this.Status = DebuggerStatus.Paused;
        }

        public void Stop()
        {
            this.Status = DebuggerStatus.Stopped;
            try
            {
                if (this.ScriptThread != null)
                {
                    this.ScriptThread.Abort();
                }
            }
            finally
            {
                this.ScriptThread = null;
            }
        }

        public DebuggerCaps GetDebuggerCaps()
        {
            return DebuggerCaps.CanDebugSourceCode | DebuggerCaps.HasLineBasedBreakpoints;
        }

        public void SetDebugService(DebugService debugService)
        {
        }

        public void SetSourceCode(SourceCode sourceCode)
        {
        }

        public void SetByteCode(string[] byteCode)
        {
        }

        public bool IsPauseRequested()
        {
            return true;
        }

        public bool SignalRuntimeException(ScriptRuntimeException ex)
        {
            MessageBoxEx.Show(MainForm.TheMainForm, ex.ToString());
            throw ex;
        }

        public DebuggerAction GetAction(int ip, SourceRef sourceref)
        {
            switch (this.Status)
            {
                case DebuggerStatus.Idle:
                case DebuggerStatus.Paused:
                case DebuggerStatus.Stopped:
                    return new DebuggerAction()
                    {
                        Action = DebuggerAction.ActionType.None,
                    };
                case DebuggerStatus.Running:
                    return new DebuggerAction()
                    {
                        Action = DebuggerAction.ActionType.Run,
                    };
            }
            throw new NotImplementedException();
        }

        public void SignalExecutionEnded()
        {
        }

        public void Update(WatchType watchType, IEnumerable<WatchItem> items)
        {
            if (items != null && watchType == WatchType.Locals)
            {
                lock (this.varsLock)
                {
                    this.vars = new Dictionary<string, string>();
                    foreach (WatchItem watchItem in items)
                    {
                        if (!string.IsNullOrEmpty(watchItem.Name) && null != watchItem.Value)
                        {
                            switch (watchItem.Value.Type)
                            {
                                case DataType.Tuple:
                                    for (int i = 0; i < watchItem.Value.Tuple.Length; i++)
                                        this.vars.Add(watchItem.Value + "[" + i.ToString() + "]", (watchItem.Value.Tuple[i] ?? DynValue.Void).ToDebugPrintString());
                                    break;
                                case DataType.Function:
                                    break;
                                case DataType.Table:
                                    foreach (TablePair p in watchItem.Value.Table.Pairs)
                                    {
                                        switch (p.Value.Type)
                                        {
                                            case DataType.Tuple:
                                            case DataType.Function:
                                            case DataType.Table:
                                            case DataType.UserData:
                                            case DataType.Thread:
                                            case DataType.ClrFunction:
                                            case DataType.TailCallRequest:
                                            case DataType.YieldRequest:
                                                break;
                                            case DataType.Nil:
                                            case DataType.Void:
                                            case DataType.Boolean:
                                            case DataType.Number:
                                            case DataType.String:
                                                this.vars.Add(watchItem.Name + "[" + p.Key.ToDebugPrintString() + "]", p.Value.ToDebugPrintString());
                                                break;
                                        }
                                    }
                                    break;
                                case DataType.UserData:
                                case DataType.Thread:
                                case DataType.ClrFunction:
                                case DataType.TailCallRequest:
                                case DataType.YieldRequest:
                                    break;
                                case DataType.Nil:
                                case DataType.Void:
                                case DataType.Boolean:
                                case DataType.Number:
                                case DataType.String:
                                default:
                                    if (watchItem.Name != "...")
                                    {
                                        this.vars.Add(watchItem.Name.ToString(), (watchItem.Value ?? DynValue.Void).ToDebugPrintString());
                                    }
                                    break;
                            }
                        }
                    }
                }
            }
        }

        public List<DynamicExpression> GetWatchItems()
        {
            return m_Dynamics;
        }

        public void RefreshBreakpoints(IEnumerable<SourceRef> refs)
        {
            throw new NotImplementedException();
        }
    }
}
