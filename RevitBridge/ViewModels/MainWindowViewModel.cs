using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using RevitBridge.Commands;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;


namespace RevitBridge.ViewModels
{
    public class MainWindowViewModel : INotifyPropertyChanged
    {
        // INotifyPropertyChanged implementation
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        // Properties
        public UserControl1ViewModel UserControl1VM { get; }

        // Commands
        public ICommand MinimizeCommand { get; }
        public ICommand MaximizeCommand { get; }
        public ICommand CloseCommand { get; }
        public ICommand DragMoveCommand { get; }

        // Constructor
        public MainWindowViewModel()
        {
            UserControl1VM = new UserControl1ViewModel();

            MinimizeCommand = new RelayCommand(MinimizeWindow);
            MaximizeCommand = new RelayCommand(MaximizeWindow);
            CloseCommand = new RelayCommand(CloseWindow);
            DragMoveCommand = new RelayCommand<Window>(DragMoveWindow);
        }

        public void Initialize(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UserControl1VM.Initialize(commandData, ref message, elements);
        }

        // Methods
        private void MinimizeWindow()
        {
            Application.Current.MainWindow.WindowState = WindowState.Minimized;
        }

        private void MaximizeWindow()
        {
            if (Application.Current.MainWindow.WindowState == WindowState.Maximized)
                Application.Current.MainWindow.WindowState = WindowState.Normal;
            else
                Application.Current.MainWindow.WindowState = WindowState.Maximized;
        }

        private void CloseWindow()
        {
            Application.Current.MainWindow.Close();
        }

        private void DragMoveWindow(Window window)
        {
            window?.DragMove();
        }
    }
}
