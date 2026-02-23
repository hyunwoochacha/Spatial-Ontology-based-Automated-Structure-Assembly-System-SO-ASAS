using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitBridgeAddin;
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using VDS.RDF.Parsing;
using VDS.RDF;
using System.Collections.Generic;
namespace RevitBridge
{
    public partial class UserControl1 : UserControl
    {
        private ExternalCommandData _commandData;
        private string _message;
        private ElementSet _elements;
        private string _ttlFilePath = @"C:\Users\chw42\source\repos\RevitBridge2\RevitBridge2\bin\Debug\WBS_Bridge.ttl"; // Default TTL file path

        public UserControl1()
        {
            InitializeComponent();
            _ttlFilePath = @"C:\Users\chw42\source\repos\RevitBridge2\RevitBridge2\bin\Debug\WBS_Bridge.ttl"; // Set default TTL file path
            TtlFilePathTextBox.Text = _ttlFilePath; // Display default path in text box

        }



        public static void Initialize(ExternalCommandData commandData, ref string message, ElementSet elements)
        {

        }


        private void MainTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            TreeViewItem selectedItem = MainTreeView.SelectedItem as TreeViewItem;
            if (selectedItem != null && selectedItem.Tag != null)
            {
                MessageBox.Show($"Selected item: {selectedItem.Tag}");

                string tag = selectedItem.Tag.ToString();

                switch (tag)
                {
                    case "Structure_Pier_Superstructure_Abutment":
                        // Execute Pier - Superstructure - Abutment placement
                        MessageBox.Show("Starting Pier - Superstructure - Abutment placement.");
                        CombineBridgeElements_Pier_Abutment command_PA = new CombineBridgeElements_Pier_Abutment();
                        command_PA.Execute(_commandData, ref _message, _elements);
                        break;

                    case "Structure_Pier_Superstructure_Pier":
                        // Execute Pier - Superstructure - Pier placement
                        MessageBox.Show("Starting Pier - Superstructure - Pier placement.");
                        CombineBridgeElements_Pier_Pier command_PP = new CombineBridgeElements_Pier_Pier();
                        command_PP.Execute(_commandData, ref _message, _elements);
                        break;

                    case "Structure_Abutment_Superstructure_Abutment":
                        // Execute Abutment - Superstructure - Abutment placement
                        MessageBox.Show("Starting Abutment - Superstructure - Abutment placement.");
                        CombineBridgeElements_Abutment_Abutment command_AA = new CombineBridgeElements_Abutment_Abutment();
                        command_AA.Execute(_commandData, ref _message, _elements);
                        break;

                    case "Structure_Abutment_Superstructure_Pier":
                        // Execute Abutment - Superstructure - Pier placement
                        MessageBox.Show("Starting Abutment - Superstructure - Pier placement.");
                        CombineBridgeElements_Abutment_Pier command_AP = new CombineBridgeElements_Abutment_Pier();
                        command_AP.Execute(_commandData, ref _message, _elements);
                        break;

                    case "Object_Pier":
                        // Execute Pier placement
                        MessageBox.Show("Starting Pier placement.");
                        ArrangePierElements command_Pier = new ArrangePierElements(_ttlFilePath);
                        command_Pier.Execute(_commandData, ref _message, _elements);
                        break;

                    case "Object_Abutment":
                        // Execute Abutment placement
                        MessageBox.Show("Starting Abutment placement.");
                        ArrangeAbutmentElements command = new ArrangeAbutmentElements(_ttlFilePath);
                        command.Execute(_commandData, ref _message, _elements);
                        break;

                    case "Object_Superstructure":
                        // Execute Superstructure placement
                        MessageBox.Show("Starting Superstructure placement.");
                        ArrangeSuperStructureElements command_Superstructure = new ArrangeSuperStructureElements(_ttlFilePath);
                        command_Superstructure.Execute(_commandData, ref _message, _elements);
                        break;

                    case "CombineAll":
                        // Execute full combination
                        MessageBox.Show("Starting full combination.");

                        var combinationActions = new List<Action>
    {
        () =>
        {
            MessageBox.Show("Trying Pier - Superstructure - Pier placement.");
            var commandPierPier = new CombineBridgeElements_Pier_Pier();
            commandPierPier.Execute(_commandData, ref _message, _elements);
        },
        () =>
        {
            MessageBox.Show("Trying Abutment - Superstructure - Abutment placement.");
            var commandAbutmentAbutment = new CombineBridgeElements_Abutment_Abutment();
            commandAbutmentAbutment.Execute(_commandData, ref _message, _elements);
        },
        () =>
        {
            MessageBox.Show("Trying Pier - Superstructure - Abutment placement.");
            var commandPierAbutment = new CombineBridgeElements_Pier_Abutment();
            commandPierAbutment.Execute(_commandData, ref _message, _elements);
        },
        () =>
        {
            MessageBox.Show("Trying Abutment - Superstructure - Pier placement.");
            var commandAbutmentPier = new CombineBridgeElements_Abutment_Pier();
            commandAbutmentPier.Execute(_commandData, ref _message, _elements);
        }
    };

                        bool isCombined = false;

                        foreach (var action in combinationActions)
                        {
                            try
                            {
                                action.Invoke(); // Attempt combination
                                isCombined = true; // Mark as successful
                                MessageBox.Show("Combination completed successfully.");
                                break; // Exit loop on success
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show($"Combination failed: {ex.Message}");
                            }
                        }

                        if (!isCombined)
                        {
                            MessageBox.Show("All combination methods failed. Please check the configuration.");
                        }

                        // Auto-close window
                        Window parentWindow = Window.GetWindow(this);
                        if (parentWindow != null)
                        {
                            parentWindow.Close();
                        }
                        break;
                }
            }
        }

        private void GetStartedButton_Click(object sender, RoutedEventArgs e)
        {
            // Button click handler
            string ttlFilePath = TtlFilePathTextBox.Text;

            if (string.IsNullOrWhiteSpace(ttlFilePath))
            {
                ttlFilePath = @"C:\Users\chw42\source\repos\RevitBridge2\RevitBridge2\bin\Debug\WBS_Bridge.ttl"; // Default TTL file path
                MessageBox.Show($"TTL file path not set. Using default path: {ttlFilePath}");
            }
            else
            {
                MessageBox.Show($"TTL file path: {ttlFilePath}");
            }

            // Load TTL file or proceed with next operation
        }




        private void BrowseTtlFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "TTL files (*.ttl)|*.ttl|All files (*.*)|*.*"
                };

                if (openFileDialog.ShowDialog() == true)
                {
                    _ttlFilePath = openFileDialog.FileName;
                    TtlFilePathTextBox.Text = _ttlFilePath;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error selecting TTL file: {ex.Message}");
            }
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {

        }

        private void Button_Click_1()
        {

        }


    }
}
