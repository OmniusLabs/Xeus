using System;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Text;
using Omnius.Core;
using Reactive.Bindings;
using Reactive.Bindings.Extensions;

namespace Omnius.Xeus.Ui.Desktop.ViewModels
{
    public class MainWindowViewModel : DisposableBase
    {
        private CompositeDisposable _disposables = new();

        public MainWindowViewModel()
        {
            this.TreeViewWidth = new ReactivePropertySlim<double>().AddTo(_disposables);
        }

        protected override void OnDispose(bool disposing)
        {
            _disposables.Dispose();
        }

        public ReactivePropertySlim<double> TreeViewWidth { get; }
    }
}
