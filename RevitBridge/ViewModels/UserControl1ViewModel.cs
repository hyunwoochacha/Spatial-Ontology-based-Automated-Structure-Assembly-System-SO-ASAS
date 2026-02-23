using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VDS.RDF;
using VDS.RDF.Parsing;
using RevitBridge.Commands;
using RevitBridge.Models;



namespace RevitBridge.ViewModels
{
    public class UserControl1ViewModel : INotifyPropertyChanged
    {
        // INotifyPropertyChanged implementation
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));


        private ExternalCommandData _commandData;
        private string _message;
        private ElementSet _elements;
        private string _ttlFilePath;

        // Properties
        public string TtlFilePath
        {
            get => _ttlFilePath;
            set
            {
                _ttlFilePath = value;
                OnPropertyChanged(nameof(TtlFilePath));
            }
        }

        // Commands
        public ICommand BrowseTtlFileCommand { get; }
        public ICommand GetStartedCommand { get; }
        public ICommand TreeViewItemSelectedCommand { get; }

        public ICommand HomeCommand { get; }
        public ICommand AnalyticsCommand { get; }
        public ICommand CalendarCommand { get; }
        public ICommand SettingsCommand { get; }

        public ObservableCollection<TreeNode> TreeNodes { get; set; }

        // Constructor

        private ExternalEvent _externalEvent;

        public UserControl1ViewModel()
        {
            TtlFilePath = @"C:\Users\chw42\source\repos\RevitBridge2\RevitBridge2\bin\Debug\WBS_Bridge.ttl"; // Default TTL file path



            // Initialize commands
            BrowseTtlFileCommand = new RelayCommand(BrowseTtlFile);
            GetStartedCommand = new RelayCommand(GetStarted);
            TreeViewItemSelectedCommand = new RelayCommand<object>(TreeViewItemSelected);

            HomeCommand = new RelayCommand(Home);
            AnalyticsCommand = new RelayCommand(Analytics);
            CalendarCommand = new RelayCommand(Calendar);
            SettingsCommand = new RelayCommand(Settings);

            // Initialize TreeNodes
            TreeNodes = new ObservableCollection<TreeNode>();
            LoadTreeData();
        }

        // Methods
        public void Initialize(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            _commandData = commandData;
            _message = message;
            _elements = elements;
        }

        private void BrowseTtlFile()
        {
            try
            {
                Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "TTL files (*.ttl)|*.ttl|All files (*.*)|*.*"
                };
                if (openFileDialog.ShowDialog() == true)
                {
                    TtlFilePath = openFileDialog.FileName;
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error selecting file: {ex.Message}");
            }
        }

        private void LoadTreeData()
        {
            // Create TreeNode structure
            var bridgeNode = new TreeNode
            {
                Name = "Bridge",
                IsExpanded = true,
                Children = new ObservableCollection<TreeNode>
        {
            new TreeNode
            {
                Name = "Rahmen Bridge",
                IsExpanded = true,
                Children = new ObservableCollection<TreeNode>
                {
                    new TreeNode
                    {
                        Name = "Structure Placement",
                        Children = new ObservableCollection<TreeNode>
                        {
                            new TreeNode { Name = "Pier - Superstructure - Pier", Tag = "Structure_Pier_Superstructure_Pier" },
                            new TreeNode { Name = "Abutment - Superstructure - Abutment", Tag = "Structure_Abutment_Superstructure_Abutment" },
                            new TreeNode { Name = "Pier - Superstructure - Abutment", Tag = "Structure_Pier_Superstructure_Abutment" },
                            new TreeNode { Name = "Abutment - Superstructure - Pier", Tag = "Structure_Abutment_Superstructure_Pier" },
                        }
                    },
                    new TreeNode
                    {
                        Name = "Object Placement",
                        Children = new ObservableCollection<TreeNode>
                        {
                            new TreeNode { Name = "Place Pier", Tag = "Object_Pier" },
                            new TreeNode { Name = "Place Abutment", Tag = "Object_Abutment" },
                            new TreeNode { Name = "Place Superstructure", Tag = "Object_Superstructure" },
                        }
                    },
                    new TreeNode { Name = "Combine All", Tag = "CombineAll" },
                    new TreeNode { Name = "Cancel", Tag = "Cancel" },
                }
            },
            new TreeNode { Name = "T-Girder Bridge (Planned)", IsEnabled = false },
            new TreeNode { Name = "Cable-Stayed Bridge (Planned)", IsEnabled = false },
            new TreeNode { Name = "Suspension Bridge (Planned)", IsEnabled = false },
        }
            };

            TreeNodes.Add(bridgeNode);
        }

        private void GetStarted()
        {
            // Get Started button click logic
        }

        private void TreeViewItemSelected(object parameter)
        {
            var selectedItem = parameter as TreeNode;
            if (selectedItem != null && !string.IsNullOrEmpty(selectedItem.Tag))
            {


                // Raise ExternalEvent
                _externalEvent.Raise();
            }
        }

        private void Home()
        {
            // Home button logic
        }

        private void Analytics()
        {
            // Analytics button logic
        }

        private void Calendar()
        {
            // Calendar button logic
        }

        private void Settings()
        {
            // Settings button logic
        }

    }
}
