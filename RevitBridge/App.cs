#region Namespaces
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Imaging;
using System.IO;

#endregion

namespace RevitBridgeAddin
{
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                // Create ribbon tab (ignore if already exists)
                string tabName = "BridgeFile";
                try
                {
                    application.CreateRibbonTab(tabName);
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException)
                {
                    // Tab already exists - ignore
                }

                // Create ribbon panel
                RibbonPanel ribbonPanel = null;
                try
                {
                    ribbonPanel = application.CreateRibbonPanel(tabName, "Bridge");
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException)
                {
                    // Panel already exists - get existing panel
                    ribbonPanel = application.GetRibbonPanels(tabName)
                        .FirstOrDefault(panel => panel.Name == "Bridge");
                }

                if (ribbonPanel == null)
                {
                    throw new Exception("Failed to create or find the ribbon panel.");
                }

                // Create button and set properties
                PushButtonData buttonData = new PushButtonData(
                    "BridgeController",
                    "Bridge",
                    Assembly.GetExecutingAssembly().Location,
                    "RevitBridgeAddin.BridgeController");

                PushButton pushButton = ribbonPanel.AddItem(buttonData) as PushButton;

                // Set button icon
                try
                {
                    string imagePath = @"C:\Users\chw42\source\repos\RevitBridge\Bridgeimage.png";
                    if (File.Exists(imagePath))
                    {
                        BitmapImage largeImage = new BitmapImage();
                        largeImage.BeginInit();
                        largeImage.UriSource = new Uri(imagePath, UriKind.Absolute);
                        largeImage.DecodePixelWidth = 32;
                        largeImage.DecodePixelHeight = 32;
                        largeImage.EndInit();
                        pushButton.LargeImage = largeImage;
                    }
                    else
                    {
                        TaskDialog.Show("Warning", "Image file not found.");
                    }
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("Warning", $"Failed to load button image: {ex.Message}");
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"An error occurred during startup: {ex.Message}");
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }
    }
}
