using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace Ven4Tools.Models
{
    public class Preset : INotifyPropertyChanged
    {
        public int     Id          { get; set; }
        public string  Name        { get; set; } = "";
        public string  Description { get; set; } = "";
        public List<string> Apps   { get; set; } = new();
        [JsonProperty("share_code")]
        public string? ShareCode   { get; set; }
        public bool    IsLocal     { get; set; }

        public string AppCountLabel => $"{Apps.Count} прил.";

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
