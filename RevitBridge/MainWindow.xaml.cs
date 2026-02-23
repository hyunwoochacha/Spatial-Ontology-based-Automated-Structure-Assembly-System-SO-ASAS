using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitBridgeAddin;
using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using RevitBridgeAddin;
using System.Linq;
using VDS.RDF.Parsing;
using VDS.RDF.Query;
using VDS.RDF;


namespace RevitBridge
{
    public partial class MainWindow : Window
    {
        private ExternalCommandData _commandData;
        private string _message;
        private ElementSet _elements;
        private string _ttlFilePath = @"C:\Users\chw42\source\repos\RevitBridge2\RevitBridge2\bin\Debug\WBS_Bridge.ttl"; // Default TTL file path

        public MainWindow(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            InitializeComponent();
            _commandData = commandData;
            _message = message;
            _elements = elements;
            TtlFilePathTextBox.Text = _ttlFilePath; // Set default TTL file path
        }

        private void MainTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            TreeViewItem selectedItem = MainTreeView.SelectedItem as TreeViewItem;
            if (selectedItem != null && selectedItem.Tag != null)
            {
                string tag = selectedItem.Tag.ToString();
                MessageBox.Show($"Selected item: {tag}");

                if (tag == "Object_Pier")
                {
                    // Pier full/partial combination UI
                    CombinationChoiceWindow choiceWindow = new CombinationChoiceWindow(_commandData, _ttlFilePath, ref _message, _elements);
                    bool? result = choiceWindow.ShowDialog();
                    if (result == true)
                    {
                        if (choiceWindow.IsFullCombination)
                        {
                            // Full combination: execute command directly
                            ExecuteCommand(new ArrangePierElements(_ttlFilePath));
                        }
                        else
                        {
                            // Partial combination: select specific pier via PierSelectionWindow
                            List<string> dynamicPierNames = GetPierListFromTTL(_ttlFilePath);

                            // User selects a pier via PierSelectionWindow
                            PierSelectionWindow pierWindow = new PierSelectionWindow(dynamicPierNames);
                            bool? pierResult = pierWindow.ShowDialog();
                            if (pierResult == true && !string.IsNullOrEmpty(pierWindow.SelectedPierName))
                            {
                                string selectedPier = pierWindow.SelectedPierName;
                                MessageBox.Show($"Partial combination selected: {selectedPier}");

                                // Pass selected pier as argument for partial combination
                                ExecuteCommand(new ArrangePierElements(_ttlFilePath, selectedPier));
                            }
                        }
                    }
                }
                else if (tag == "Object_Abutment")
                {
                    // Abutment full/partial combination UI
                    CombinationChoiceWindow choiceWindow = new CombinationChoiceWindow(_commandData, _ttlFilePath, ref _message, _elements);
                    bool? result = choiceWindow.ShowDialog();
                    if (result == true)
                    {
                        if (choiceWindow.IsFullCombination)
                        {
                            ExecuteCommand(new ArrangeAbutmentElements(_ttlFilePath));
                        }
                        else
                        {
                            AbutmentSelectionWindow abutmentWindow = new AbutmentSelectionWindow(_commandData, _ttlFilePath, ref _message, _elements);
                            bool? abutmentResult = abutmentWindow.ShowDialog();
                            if (abutmentResult == true)
                            {
                                string selectedAbutment = abutmentWindow.IsA1Selected ? "A1" : "A2";
                                MessageBox.Show($"Partial combination selected: {selectedAbutment}");
                                // Call constructor with selectedAbutment for partial combination
                                ExecuteCommand(new ArrangeAbutmentElements(_ttlFilePath, selectedAbutment));
                                
                            }
                        }
                    }
                }
                else
                {
                    // Object_Pier, Object_Abutment cases removed (handled above)
                    switch (tag)
                    {
                        case "Structure_Pier_Superstructure_Abutment":
                            ExecuteCommand(new CombineBridgeElements_Pier_Abutment());
                            break;
                        case "Structure_Pier_Superstructure_Pier":
                            ExecuteCommand(new CombineBridgeElements_Pier_Pier());
                            break;
                        case "Structure_Abutment_Superstructure_Abutment":
                            ExecuteCommand(new CombineBridgeElements_Abutment_Abutment());
                            break;
                        case "Structure_Abutment_Superstructure_Pier":
                            ExecuteCommand(new CombineBridgeElements_Abutment_Pier());
                            break;
                        case "Object_Superstructure":
                            ExecuteCommand(new ArrangeSuperStructureElements(_ttlFilePath));
                            break;
                        case "CombineAll":
                            CombineAllElements();
                            break;
                        default:
                            MessageBox.Show("Unknown selection.");
                            break;
                    }
                }
            }
        }






        /// <summary>
        /// Executes a Revit external command and returns success status.
        /// </summary>
        private bool ExecuteCommand(IExternalCommand command)
        {
            _message = "";                             // Reset message before each command
            Result r = command.Execute(_commandData, ref _message, _elements);

            // Check both Result and message
            bool isSuccess = r == Result.Succeeded &&
                             string.IsNullOrWhiteSpace(_message);

            MessageBox.Show(isSuccess
                            ? "Mission Start!"
                            : $"Mission Failed: {_message}");

            return isSuccess;
        }

        private void CombineAllElements()
        {
            // Array of (display title, command object) tuples
            var tries = new (string title, IExternalCommand cmd)[]
            {
        ("Pier – Pier",         new CombineBridgeElements_Pier_Pier()),
        ("Pier – Abutment",     new CombineBridgeElements_Pier_Abutment()),
        ("Abutment – Pier",     new CombineBridgeElements_Abutment_Pier()),
        ("Abutment – Abutment", new CombineBridgeElements_Abutment_Abutment())
            };

            List<string> succeeded = new List<string>();
            List<string> failed = new List<string>();

            foreach (var (title, cmd) in tries)
            {
                bool ok = ExecuteCommand(cmd);

                if (ok)
                    succeeded.Add(title);
                else
                    failed.Add(title);
            }

            if (succeeded.Count > 0)
                MessageBox.Show($"Succeeded: {string.Join(", ", succeeded)}", "Combine All Elements");
            else
                MessageBox.Show("All combination attempts failed.", "Combine All Elements");
        }

        private void BrowseTtlFile_Click(object sender, RoutedEventArgs e)
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

        // Close button click event
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        // Minimize button click event
        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        // Maximize button click event
        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.WindowState == WindowState.Maximized)
                this.WindowState = WindowState.Normal;
            else
                this.WindowState = WindowState.Maximized;
        }

        // Title bar drag move event
        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
        }

        // Home button click event
        private void HomeButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Home button clicked.");
        }

        private void GetStartedButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_ttlFilePath))
            {
                MessageBox.Show("TTL file path is required.");
            }
            else
            {
                MessageBox.Show($"TTL file path: {_ttlFilePath}");
            }
        }
        private void PlayVideo_Click(object sender, RoutedEventArgs e)
        {
            string videoPath = "C:\\Users\\chw42\\Videos\\Bandicam\\ObjectCombination\\Pier-Pier.mp4";

            if (System.IO.File.Exists(videoPath))
            {
                VideoPlayer.Source = new Uri(videoPath);
                VideoPlayer.LoadedBehavior = MediaState.Manual;
                VideoPlayer.UnloadedBehavior = MediaState.Stop;
                VideoPlayer.Play();
                MessageBox.Show("Playing video.");
            }
            else
            {
                MessageBox.Show("Video file not found.");
            }
        }

        private List<string> GetPierListFromTTL(string ttlFilePath)
        {
            List<string> pierNames = new List<string>();

            // Load TTL file
            Graph g = new Graph();
            FileLoader.Load(g, ttlFilePath);

            // SPARQL query to find all instances of ex:Pier and its subclasses
            // Uses rdfs:subClassOf* to include all subclasses of ex:Pier
            string query = @"
    PREFIX ex: <http://example.org/>
    PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>
    PREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#>

    SELECT DISTINCT ?instance WHERE {
      ?instance rdf:type ?pierClass .
      ?pierClass rdfs:subClassOf* ex:Pier .
    }";

            SparqlResultSet results = (SparqlResultSet)g.ExecuteQuery(query);

            // Parse URI to extract pier identifiers (A1, A2, A3...)
            // Instance URI format: ex:A1_PierCoping_Instance, ex:A2_PierFooting_Instance, etc.
            // Using regex to extract the 'A{n}' portion
            System.Text.RegularExpressions.Regex regex = new System.Text.RegularExpressions.Regex(@"(A[0-9]+)_Pier");

            HashSet<string> uniquePiers = new HashSet<string>();
            foreach (SparqlResult result in results)
            {
                string instanceUri = result["instance"].ToString(); // e.g., "http://example.org/A1_PierCoping_Instance"
                var match = regex.Match(instanceUri);
                if (match.Success)
                {
                    string pierId = match.Groups[1].Value; // e.g., A1
                    uniquePiers.Add(pierId);
                }
            }

            // Convert HashSet to sorted list
            pierNames = uniquePiers.ToList();
            pierNames.Sort(); // Sort (A1, A2, A3 order)

            return pierNames;
        }

    }
}