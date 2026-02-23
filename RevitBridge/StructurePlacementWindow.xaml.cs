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
    public partial class StructurePlacementWindow : Window
    {
        private ExternalCommandData _commandData;
        private string _message;
        private ElementSet _elements;

        public StructurePlacementWindow(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            InitializeComponent();
            _commandData = commandData;
            _message = message;
            _elements = elements;
        }

        private void AbutmentSuperstructureAbutment_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Starting Abutment - Superstructure - Abutment placement.");
            CombineBridgeElements_Abutment_Abutment combineBridge_AA = new CombineBridgeElements_Abutment_Abutment();
            combineBridge_AA.Execute(_commandData, ref _message, _elements);
            this.Close();
        }

        private void AbutmentSuperstructurePier_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Starting Abutment - Superstructure - Pier placement.");
            CombineBridgeElements_Abutment_Pier combineBridge_AP = new CombineBridgeElements_Abutment_Pier();
            combineBridge_AP.Execute(_commandData, ref _message, _elements);
        }

        private void PierSuperstructureAbutment_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Starting Pier - Superstructure - Abutment placement.");
            CombineBridgeElements_Pier_Abutment combineBridge_PA = new CombineBridgeElements_Pier_Abutment();
            combineBridge_PA.Execute(_commandData, ref _message, _elements);
        }

        private void PierSuperstructurePier_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Starting Pier - Superstructure - Pier placement.");
            CombineBridgeElements_Pier_Pier combineBridge_PP = new CombineBridgeElements_Pier_Pier();
            combineBridge_PP.Execute(_commandData, ref _message, _elements);
        }
    }
}