using RevitBridgeAddin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;


namespace RevitBridge
{
    public partial class ObjectPlacementWindow : Window
    {
        private ExternalCommandData _commandData;
        private string _message;
        private ElementSet _elements;
        private string _ttlFilePath;

        public ObjectPlacementWindow(ExternalCommandData commandData, ref string message, ElementSet elements, string ttlFilePath)
        {
            InitializeComponent();
            _commandData = commandData;
            _message = message;
            _elements = elements;
            _ttlFilePath = ttlFilePath;
        }

        private void PlacePier_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Start Pier Assembly.");

            ArrangePierElements command_Pier = new ArrangePierElements(_ttlFilePath);
            command_Pier.Execute(_commandData, ref _message, _elements);
            this.Close();

        }

        private void PlaceAbutment_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Start Abutment Assembly.");
            // Execute abutment arrangement command
            ArrangeAbutmentElements command = new ArrangeAbutmentElements(_ttlFilePath);
            command.Execute(_commandData, ref _message, _elements);
            this.Close();
        }

        private void PlaceSuperstructure_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Start Superstructure Assembly.");
            ArrangeSuperStructureElements arrangeSuperStructure = new ArrangeSuperStructureElements(_ttlFilePath);
            arrangeSuperStructure.Execute(_commandData, ref _message, _elements);
            this.Close();
        }
    }
}