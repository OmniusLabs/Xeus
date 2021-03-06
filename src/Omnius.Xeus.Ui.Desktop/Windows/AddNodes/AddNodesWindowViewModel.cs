using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using Avalonia.Controls;
using Omnius.Core;
using Omnius.Core.Avalonia;
using Omnius.Xeus.Engines.Models;
using Omnius.Xeus.Services;
using Omnius.Xeus.Ui.Desktop.Configuration;
using Reactive.Bindings;
using Reactive.Bindings.Extensions;

namespace Omnius.Xeus.Ui.Desktop.Windows.AddNodes
{
    public class AddNodesWindowViewModel : AsyncDisposableBase
    {
        private readonly UiState _uiState;
        private readonly IClipboardService _clipboardService;

        private readonly List<NodeProfile> _nodeProfiles = new();

        private readonly CompositeDisposable _disposable = new();

        public AddNodesWindowViewModel(UiState uiState, IClipboardService clipboardService)
        {
            _uiState = uiState;
            _clipboardService = clipboardService;

            this.Text = new ReactivePropertySlim<string>().AddTo(_disposable);
            this.OkCommand = new ReactiveCommand().AddTo(_disposable);
            this.OkCommand.Subscribe((state) => this.Ok(state));
            this.CancelCommand = new ReactiveCommand().AddTo(_disposable);
            this.CancelCommand.Subscribe((state) => this.Cancel(state));
        }

        public async ValueTask InitializeAsync()
        {
            this.Text.Value = await _clipboardService.GetTextAsync();
        }

        protected override async ValueTask OnDisposeAsync()
        {
            _disposable.Dispose();
        }

        public IEnumerable<NodeProfile> GetNodeProfiles() => _nodeProfiles.ToArray();

        public ReactivePropertySlim<string> Text { get; }

        public ReactiveCommand OkCommand { get; }

        public ReactiveCommand CancelCommand { get; }

        private async void Ok(object state)
        {
            _nodeProfiles.Clear();
            _nodeProfiles.AddRange(await this.ParseNodeProfilesAsync());

            var window = (Window)state;
            window.Close();
        }

        private async void Cancel(object state)
        {
            var window = (Window)state;
            window.Close();
        }

        private async ValueTask<IEnumerable<NodeProfile>> ParseNodeProfilesAsync()
        {
            var results = new List<NodeProfile>();

            foreach (var line in this.Text.Value.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()))
            {
                if (!XeusMessageConverter.TryStringToNodeProfile(line, out var nodeProfile)) continue;
                results.Add(nodeProfile);
            }

            return results;
        }
    }
}
