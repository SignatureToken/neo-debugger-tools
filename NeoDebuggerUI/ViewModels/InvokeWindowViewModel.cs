﻿using System.Linq;
using Neo.Debugger.Core.Models;
using ReactiveUI;
using System.Reactive.Linq;
using LunarLabs.Parser;
using NeoDebuggerUI.Models;
using System;
using System.Threading.Tasks;
using Neo.VM;
using System.Collections.Generic;

namespace NeoDebuggerUI.ViewModels
{
    public class InvokeWindowViewModel : ViewModelBase
    {
        public IEnumerable<string> TestCases { get => DebuggerStore.instance.Tests.cases.Keys; }
        public IEnumerable<string> FunctionList { get => DebuggerStore.instance.manager.ABI.functions.Select(x => x.Value.name); }

        public DataNode SelectedTestCaseParams => SelectedTestCase != null ? DebuggerStore.instance.Tests.cases[SelectedTestCase].args : null;
        public DebugParameters DebugParams { get; set; } = new DebugParameters();

        public delegate void SelectedTestChanged(string selectedTestCase);
        public event SelectedTestChanged EvtSelectedTestCaseChanged;

        private string _selectedTestCase;
        public string SelectedTestCase
        {
            get => _selectedTestCase;
            set => this.RaiseAndSetIfChanged(ref _selectedTestCase, value);
        }

        private string _selectedFunction;
        public string SelectedFunction
        {
            get => _selectedFunction;
            set => this.RaiseAndSetIfChanged(ref _selectedFunction, value);
        }

        public void NotifySelectedTestChangeEvt()
        {
            EvtSelectedTestCaseChanged?.Invoke(_selectedTestCase);
        }
        
        public InvokeWindowViewModel()
        {
            if(DebuggerStore.instance.Tests != null && DebuggerStore.instance.Tests.cases.Count > 0) {
                _selectedTestCase = DebuggerStore.instance.Tests.cases.ElementAt(0).Key;
            }
            
            _selectedFunction = DebuggerStore.instance.manager.ABI.entryPoint.name;

            var selectedTestChanged = this.WhenAnyValue(x => x.SelectedTestCase);
            selectedTestChanged.Subscribe(test => NotifySelectedTestChangeEvt());
        }

        public void Run()
        {
            DebugParams.TriggerType = Neo.Emulation.TriggerType.Application;
            DebuggerStore.instance.manager.ConfigureDebugParameters(DebugParams);
            DebuggerStore.instance.manager.Run();
            StackItem result = null;
            string errorMessage = null;
            try
            {
                result = DebuggerStore.instance.manager.Emulator.GetOutput();
            }
            catch(Exception ex)
            {
                errorMessage = ex.Message;
            }

            if (result != null)
            {
                OpenGenericSampleDialog("Execution finished.\nGAS cost: " + DebuggerStore.instance.UsedGasCost + "\nResult: " + result.GetString(), "OK", "", false);
            }
            else
            {
                OpenGenericSampleDialog(errorMessage, "Error", "", false);
            }
        }
    }
}