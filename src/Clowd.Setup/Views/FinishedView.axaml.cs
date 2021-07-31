using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Clowd.Setup.Views
{
    public class FinishedViewModel : ViewModelBase
    {
        public string Title
        {
            get => _title;
            set => RaiseAndSetIfChanged(ref _title, value);
        }

        public string Body
        {
            get => _body;
            set => RaiseAndSetIfChanged(ref _body, value);
        }

        public bool CanStartClowd
        {
            get => _canStartClowd;
            set => RaiseAndSetIfChanged(ref _canStartClowd, value);
        }

        public string ClowdExePath
        {
            get => _clowdExePath;
            set => RaiseAndSetIfChanged(ref _clowdExePath, value);
        }

        private string _title = "Done";
        private string _body = "Thanks for using Clowd!";
        private bool _canStartClowd;
        private string _clowdExePath;
    }

    public partial class FinishedView : UserControl
    {
        public FinishedView() : this(new FinishedViewModel())
        {
        }

        public FinishedView(FinishedViewModel model)
        {
            this.DataContext = model;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            Environment.Exit(0);
        }
    }
}
