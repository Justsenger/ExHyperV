using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;

namespace ExHyperV.Models
{
    public partial class VMInfo : ObservableObject
    {
        [ObservableProperty]
        private string _name;

        [ObservableProperty]
        private string _lowMMIO;

        [ObservableProperty]
        private string _highMMIO;

        [ObservableProperty]
        private string _guestControlled;

        [ObservableProperty]
        private Dictionary<string, string> _gPUs;

        [ObservableProperty]
        private int _generation;

        [ObservableProperty]
        private bool _isRunning;

        public VMInfo(string name, string low, string high, string guest, Dictionary<string, string> gpus, int generation = 0, bool isRunning = false)
        {
            _name = name;
            _lowMMIO = low;
            _highMMIO = high;
            _guestControlled = guest;
            _gPUs = gpus;
            _generation = generation;
            _isRunning = isRunning;
        }
    }
}