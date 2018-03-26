﻿using Neo.VM;
using Neo.Cryptography;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Numerics;
using Neo.Emulation.API;
using LunarParser;
using Neo.Emulation.Utils;
using System.Diagnostics;

namespace Neo.Emulation
{
    public enum CheckWitnessMode
    {
        Default,
        AlwaysTrue,
        AlwaysFalse
    }

    public struct DebuggerState
    {
        public enum State
        {
            Invalid,
            Reset,
            Running,
            Finished,
            Exception,
            Break
        }

        public readonly State state;
        public readonly int offset;

        public DebuggerState(State state, int offset)
        {
            this.state = state;
            this.offset = offset;
        }
    }

    public static class NeoEmulatorExtensions
    {
        public static Emulator GetEmulator(this ExecutionEngine engine)
        {
            var tx  = (Transaction)engine.ScriptContainer;
            return tx.emulator;
        }

        public static Account GetAccount(this ExecutionEngine engine)
        {
            var emulator = engine.GetEmulator();
            return emulator.currentAccount;
        }

        public static Blockchain GetBlockchain(this ExecutionEngine engine)
        {
            var emulator = engine.GetEmulator();
            return emulator.blockchain;
        }

        public static Storage GetStorage(this ExecutionEngine engine)
        {
            var emulator = engine.GetEmulator();
            return emulator.currentAccount.storage;
        }
    }

    public class Emulator 
    {
        public enum Type
        {
            Unknown,
            String,
            Boolean,
            Integer,
            Array,
            ByteArray
        }

        public class Variable
        {
            public StackItem value;
            public Type type;
        }

        public struct Assignment
        {
            public string name;
            public Type type;
        }

        public struct EmulatorStepInfo
        {
            public byte[] byteCode;
            public int offset;
            public OpCode opcode;
            public decimal gasCost;
            public string sysCall;
        }

        private ExecutionEngine engine;
        public byte[] contractByteCode { get; private set; }

        private InteropService interop;

        private HashSet<int> _breakpoints = new HashSet<int>();
        public IEnumerable<int> Breakpoints { get { return _breakpoints; } }

        public Blockchain blockchain { get; private set; }

        private DebuggerState lastState = new DebuggerState(DebuggerState.State.Invalid, -1);

        public Account currentAccount { get; private set; }
        public Transaction currentTransaction { get; private set; }

        private UInt160 currentHash;

        public CheckWitnessMode checkWitnessMode = CheckWitnessMode.Default;
        public TriggerType currentTrigger = TriggerType.Application;
        public uint timestamp = DateTime.Now.ToTimestamp();

        public decimal usedGas { get; private set; }
        public int usedOpcodeCount { get; private set; }

        public Action<EmulatorStepInfo> OnStep;

        public Emulator(Blockchain blockchain)
        {
            this.blockchain = blockchain;
            this.interop = new InteropService();            
        }

        public int GetInstructionPtr()
        {
            return engine.CurrentContext.InstructionPointer;
        }

        public void SetExecutingAccount(Account address)
        {
            this.currentAccount = address;
            this.contractByteCode = address.byteCode;
        }

        private int lastOffset = -1;

        private static void EmitObject(ScriptBuilder sb, object item)
        {
            if (item is byte[])
            {
                var arr = (byte[])item;

                for (int index = arr.Length - 1; index >= 0; index--)
                {
                    sb.EmitPush(arr[index]);
                }

                sb.EmitPush(arr.Length);
                sb.Emit(OpCode.PACK);
            }
            else
            if (item is List<object>)
            {
                var list = (List<object>)item;

                for (int index = 0; index < list.Count; index++)
                {
                    EmitObject(sb, list[index]);
                }              

                sb.EmitPush(list.Count);
                sb.Emit(OpCode.PACK);

                /*sb.Emit((OpCode)((int)OpCode.PUSHT + list.Count - 1));
                sb.Emit(OpCode.NEWARRAY);

                for (int index = 0; index < list.Count; index++)
                {
                    sb.Emit(OpCode.DUP); // duplicates array reference into top of stack
                    sb.EmitPush(new BigInteger(index));
                    EmitObject(sb, list[index]);
                    sb.Emit(OpCode.SETITEM);
                }*/
            }
            else
            if (item == null)
            {
                sb.EmitPush("");
            }
            else
            if (item is string)
            {
                sb.EmitPush((string)item);
            }
            else
            if (item is bool)
            {
                sb.EmitPush((bool)item);
            }
            else
            if (item is BigInteger)
            {
                sb.EmitPush((BigInteger)item);
            }
            else
            {
                throw new Exception("Unsupport contract param: " + item.ToString());
            }
        }

        private ABI _ABI;

        public void Reset(DataNode inputs, ABI ABI)
        {
            if (contractByteCode == null || contractByteCode.Length == 0)
            {
                throw new Exception("Contract bytecode is not set yet!");
            }
            
            if (lastState.state == DebuggerState.State.Reset)
            {
                return;
            }

            if (currentTransaction == null)
            {
                //throw new Exception("Transaction not set");
                currentTransaction = new Transaction(this.blockchain.currentBlock);
            }

            usedGas = 0;
            usedOpcodeCount = 0;

            currentTransaction.emulator = this;
            engine = new ExecutionEngine(currentTransaction, Crypto.Default, null, interop);
            engine.LoadScript(contractByteCode);

            foreach (var output in currentTransaction.outputs)
            {
                if (output.hash == this.currentHash)
                {
                    output.hash = new UInt160(engine.CurrentContext.ScriptHash);
                }
            }

            foreach (var pos in _breakpoints)
            {
                engine.AddBreakPoint((uint)pos);
            }

            using (ScriptBuilder sb = new ScriptBuilder())
            {
                var items = new Stack<object>();

                if (inputs != null)
                {
                    foreach (var item in inputs.Children)
                    {
                        var obj = Emulator.ConvertArgument(item);
                        items.Push(obj);
                    }
                }

                while (items.Count > 0)
                {
                    var item = items.Pop();
                    EmitObject(sb, item);
                }

                var loaderScript = sb.ToArray();
                //System.IO.File.WriteAllBytes("loader.avm", loaderScript);
                engine.LoadScript(loaderScript);
            }

            //engine.Reset();

            lastState = new DebuggerState(DebuggerState.State.Reset, 0);
            currentTransaction = null;

            _variables.Clear();
            this._ABI = ABI;
        }

        public void SetBreakpointState(int ofs, bool enabled)
        {
            if (enabled)
            {
                _breakpoints.Add(ofs);
            }
            else
            {
                _breakpoints.Remove(ofs);
            }
        }

        public bool GetRunningState()
        {
            return !engine.State.HasFlag(VMState.HALT) && !engine.State.HasFlag(VMState.FAULT) && !engine.State.HasFlag(VMState.BREAK);
        }

        private bool ExecuteSingleStep()
        {
            if (this.lastState.state == DebuggerState.State.Reset)
            {
                engine.State = VMState.NONE;

                var initialContext = engine.CurrentContext;
                while (engine.CurrentContext == initialContext)
                {
                    engine.StepInto();
                }

                if (this._ABI != null && _ABI.entryPoint != null)
                {
                    int index = 0;
                    foreach (var entry in _ABI.entryPoint.inputs)
                    {
                        try
                        {
                            var val = engine.EvaluationStack.Peek(index);

                            var varType = entry.type;

                            // if the type is unknown we can always check if the type was known in a previous assigment
                            var prevVal = GetVariable(entry.name);
                            if (varType == Type.Unknown && prevVal != null)
                            {
                                varType = prevVal.type;
                            }

                            _variables[entry.name] = new Variable() { value = val, type = varType };

                            index++;
                        }
                        catch
                        {
                            break;
                        }
                    }
                }
            }

            var shouldContinue = GetRunningState();
            if (shouldContinue)
            {
                engine.StepInto();

                if (engine.State == VMState.NONE)
                {
                    int currentOffset = engine.CurrentContext.InstructionPointer;

                    if (_assigments.ContainsKey(currentOffset))
                    {
                        var ass = _assigments[currentOffset];
                        try
                        {
                            var val = engine.EvaluationStack.Peek();
                            _variables[ass.name] =  new Variable() { value = val, type = ass.type };
                        }
                        catch
                        {
                            // ignore for now
                        }
                    }
                }

                return GetRunningState();
            }
            else
            {
                return false;
            }
        }
        
        /// <summary>
        /// executes a single instruction in the current script, and returns the last script offset
        /// </summary>
        public DebuggerState Step()
        {
            if (lastState.state == DebuggerState.State.Finished || lastState.state == DebuggerState.State.Invalid)
            {
                return lastState;
            }

            ExecuteSingleStep();

            try
            {
                lastOffset = engine.CurrentContext.InstructionPointer;

                var opcode = engine.lastOpcode;
                decimal opCost;

                if (opcode <= OpCode.PUSH16)
                {
                    opCost = 0;
                }
                else
                    switch (opcode)
                    {
                        case OpCode.SYSCALL:
                            {
                                var callInfo = interop.FindCall(engine.lastSysCall);
                                opCost = (callInfo != null) ? callInfo.gasCost : 0;

                                if (engine.lastSysCall.EndsWith("Storage.Put"))
                                {
                                    opCost *= (Storage.lastStorageLength / 1024.0m);
                                    if (opCost < 1) opCost = 1;
                                }
                                break;
                            }

                        case OpCode.CHECKMULTISIG:
                        case OpCode.CHECKSIG: opCost = 0.1m; break;

                        case OpCode.APPCALL:
                        case OpCode.TAILCALL:
                        case OpCode.SHA256:
                        case OpCode.SHA1: opCost = 0.01m; break;

                        case OpCode.HASH256:
                        case OpCode.HASH160: opCost = 0.02m; break;

                        case OpCode.NOP: opCost = 0; break;
                        default: opCost = 0.001m; break;
                    }

                usedGas += opCost;
                usedOpcodeCount++;

                OnStep?.Invoke(new EmulatorStepInfo() { byteCode = engine.CurrentContext.Script, offset = engine.CurrentContext.InstructionPointer, opcode = opcode, gasCost = opCost, sysCall = opcode == OpCode.SYSCALL? engine.lastSysCall : null });
            }
            catch
            {
                // failed to get instruction pointer
            }

            if (engine.State.HasFlag(VMState.FAULT))
            {
                lastState = new DebuggerState(DebuggerState.State.Exception, lastOffset);
                return lastState;
            }

            if (engine.State.HasFlag(VMState.BREAK))
            {
                lastState = new DebuggerState(DebuggerState.State.Break, lastOffset);
                engine.State = VMState.NONE;
                return lastState;
            }

            if (engine.State.HasFlag(VMState.HALT))
            {
                lastState = new DebuggerState(DebuggerState.State.Finished, lastOffset);
                return lastState;
            }

            lastState = new DebuggerState(DebuggerState.State.Running, lastOffset);
            return lastState;
        }

        /// <summary>
        /// executes the script until it finishes, fails or hits a breakpoint
        /// </summary>
        public DebuggerState Run()
        {
            do
            {
                lastState = Step();
            } while (lastState.state == DebuggerState.State.Running);

            return lastState;
        }

        public StackItem GetOutput()
        {
            var result = engine.EvaluationStack.Peek();
            return result;
        }

        public IEnumerable<StackItem> GetEvaluationStack()
        {
            return engine.EvaluationStack;
        }

        public IEnumerable<StackItem> GetAltStack()
        {
            return engine.AltStack;
        }


        #region TRANSACTIONS
        public void SetTransaction(byte[] assetID, BigInteger amount)
        {
            var key = Runtime.invokerKeys;

            var bytes = key != null ? Helper.AddressToScriptHash(key.address) : new byte[20];

            var src_hash = new UInt160(bytes);
            var dst_hash = new UInt160(Helper.AddressToScriptHash(this.currentAccount.keys.address));
            this.currentHash = dst_hash;

            BigInteger asset_decimals = 100000000;
            BigInteger total_amount = (amount * 10) * asset_decimals; // FIXME instead of (amount * 10) we should take balance from virtual blockchain

            var block = blockchain.GenerateBlock();

            var tx = new Transaction(block);
            //tx.inputs.Add(new TransactionInput(-1, src_hash));
            tx.outputs.Add(new TransactionOutput(assetID, amount, dst_hash));
            tx.outputs.Add(new TransactionOutput(assetID, total_amount - amount, src_hash));

            blockchain.ConfirmBlock(block);
          
            this.currentTransaction = tx;
        }
        #endregion

        public static object ConvertArgument(DataNode item)
        {
            if (item.HasChildren)
            {
                bool isByteArray = true;

                foreach (var child in item.Children)
                {
                    byte n;
                    if (string.IsNullOrEmpty(child.Value) || !byte.TryParse(child.Value, out n))
                    {
                        isByteArray = false;
                        break;
                    }
                }

                if (isByteArray)
                {
                    var arr = new byte[item.ChildCount];
                    int index = 0;
                    foreach (var child in item.Children)
                    {
                        arr[index] = byte.Parse(child.Value);
                        index++;
                   }
                    return arr;
                }
                else
                {
                    var list = new List<object>();
                    foreach (var child in item.Children)
                    {
                        list.Add(ConvertArgument(child));
                    }
                    return list;
                }
            }

            BigInteger intVal;

            if (item.Kind == NodeKind.Numeric)
            {
                if (BigInteger.TryParse(item.Value, out intVal))
                {
                    return intVal;
                }
                else
                {
                    return 0;
                }
            }
            else
            if (item.Kind == NodeKind.Boolean)
            {
                return "true".Equals(item.Value.ToLowerInvariant()) ? true : false;
            }
            else
            if (item.Kind == NodeKind.Null)
            {
                return null;
            }
            else
            if (item.Value == null)
            {
                return null;
            }
            else
            if (item.Value.StartsWith("0x"))
            {
                return item.Value.Substring(2).HexToByte();
            }
            else
            {
                return item.Value;
            }
        }

        public byte[] GetExecutingByteCode()
        {
            try
            {
                return engine.CurrentContext.Script;
            }
            catch
            {
                return null;
            }
        }

        private Dictionary<int, Assignment> _assigments = new Dictionary<int, Assignment>();
        private Dictionary<string, Variable> _variables = new Dictionary<string, Variable>();

        public void ClearAssignments()
        {
            _assigments.Clear();
            _variables.Clear();
        }

        public void AddAssigment(int offset, string name, Type type)
        {
            _assigments[offset] = new Assignment() { name = name, type = type};
        }

        public Variable GetVariable(string name)
        {
            if (_variables.ContainsKey(name))
            {
                return _variables[name];
            }

            return null;
        }
    }
}
