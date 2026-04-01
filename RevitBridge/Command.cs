using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.IO;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Query;
using System.Collections.Generic;
using System.Linq;
using RevitBridge;
using System.Windows.Controls;
using System.Windows;
using System.Runtime.Remoting.Messaging;
using Lucene.Net.Search;



namespace RevitBridgeAddin
{
    [Transaction(TransactionMode.Manual)]
    public class BridgeController : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // Pass Revit data to MainWindow
                MainWindow mainWindow = new MainWindow(commandData, ref message, elements);
                mainWindow.ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class ArrangePierElements : IExternalCommand
    {
        private string _ttlFilePath;
        private ExternalCommandData _commandData;
        private string _message;
        private ElementSet _elements;
        private string _selectedPier; // Selected pier name for partial combination

        // Static members for action queue and state management
        private static Queue<Action> _actionQueue = new Queue<Action>();
        private static bool _isProcessing = false;
        private static DateTime _lastActionTime = DateTime.MinValue;
        private static readonly TimeSpan _delay = TimeSpan.FromMilliseconds(500); // 0.5 second delay

        // Log file path
        private static readonly string _logFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "CombinePierElements_Log.txt");

        public ArrangePierElements(string ttlFilePath)
        {
            _ttlFilePath = ttlFilePath;
        }

        public ArrangePierElements(string ttlFilePath, string selectedPier)
        {
            _ttlFilePath = ttlFilePath;
            _selectedPier = selectedPier;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            _commandData = commandData;
            _message = message;
            _elements = elements;

            // Reset static state from previous runs
            _actionQueue.Clear();
            _isProcessing = false;

            UIDocument uidoc = _commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            // Initialize log file
            File.WriteAllText(_logFilePath, $"[{DateTime.Now}] ArrangePierElements execution started.\n");

            // Load ontology file
            Graph g = new Graph();
            try
            {
                FileLoader.Load(g, _ttlFilePath);
                LogMessage("TTL file loading completed.");
            }
            catch (Exception ex)
            {
                message = $"Ontology file loading failed: {ex.Message}";
                LogMessage($"Ontology file loading failed: {ex.Message}");
                return Result.Failed;
            }


            // Process logic based on user selection
            if (string.IsNullOrEmpty(_selectedPier))
            {
                // Full combination logic
                LogMessage("Full combination logic started");
                CombineAllElements(commandData, g, doc);
                LogMessage("Full combination logic completed");
            }
            else
            {
                // Partial combination logic: use _selectedPier
                LogMessage($"Partial combination logic started - selected pier: {_selectedPier}");

                // Execute SPARQL query based on _selectedPier and process results
                string pierName = _selectedPier;
                string query = $@"
PREFIX bso: <https://hyunwoochacha.github.io/SO-ASAS/ontology#>
PREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#>
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>

SELECT ?copingName ?columnName ?foundationName ?footingName WHERE {{
    bso:{pierName}_PierCoping_Instance bso:isAttachedTo bso:{pierName}_PierColumn_Instance .
    bso:{pierName}_PierColumn_Instance bso:isAttachedTo bso:{pierName}_PierFoundation_Instance .
    bso:{pierName}_PierFoundation_Instance bso:isAttachedTo bso:{pierName}_PierFooting_Instance .
    bso:{pierName}_PierCoping_Instance bso:name ?copingName .
    bso:{pierName}_PierColumn_Instance bso:name ?columnName .
    bso:{pierName}_PierFoundation_Instance bso:name ?foundationName .
    bso:{pierName}_PierFooting_Instance bso:name ?footingName .
}}";

                SparqlResultSet results = ExecutePierQuery(g, query, doc, pierName);
                if (results != null && results.Count > 0)
                {
                    // Partial combination: perform combination on user-selected pier
                    // Previously had Yes/No dialog, now auto-executes
                    // ShowCombineDialog removed, direct combination
                    ProcessPierResults(results, doc, pierName);
                    LogMessage("Partial combination logic completed");
                }
                else
                {
                    TaskDialog.Show("Error", $"{pierName}Cannot find data for.");
                    LogMessage($"{pierName}Cannot find data for.");
                }
            }

            return Result.Succeeded;
        }

        // Full combination logic: add combination steps to action queue
        // CombineAllElements method processes A1~A7 piers iteratively
        private void CombineAllElements(ExternalCommandData commandData, Graph g, Document doc)
        {
            // List to store found elements per pier
            List<string> foundElements = new List<string>();

            // Process A1~A7 piers iteratively
            for (int i = 1; i <= 7; i++)
            {
                string pierName = $"A{i}";

                // Generate SPARQL query for each pier
                string query = $@"
PREFIX bso: <https://hyunwoochacha.github.io/SO-ASAS/ontology#>
PREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#>
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>

SELECT ?copingName ?columnName ?foundationName ?footingName WHERE {{
    bso:{pierName}_PierCoping_Instance bso:isAttachedTo bso:{pierName}_PierColumn_Instance .
    bso:{pierName}_PierColumn_Instance bso:isAttachedTo bso:{pierName}_PierFoundation_Instance .
    bso:{pierName}_PierFoundation_Instance bso:isAttachedTo bso:{pierName}_PierFooting_Instance .
    bso:{pierName}_PierCoping_Instance bso:name ?copingName .
    bso:{pierName}_PierColumn_Instance bso:name ?columnName .
    bso:{pierName}_PierFoundation_Instance bso:name ?foundationName .
    bso:{pierName}_PierFooting_Instance bso:name ?footingName .
}}";

                // Execute SPARQL query
                SparqlResultSet results = ExecutePierQuery(g, query, doc, pierName);

                // Process results and add combination steps to queue
                if (results != null && results.Count > 0)
                {
                    EnqueueCombineActions(doc, results, pierName);
                    foreach (SparqlResult result in results)
                    {
                        string copingName = result["copingName"].ToString().Split('^')[0];
                        string columnName = result["columnName"].ToString().Split('^')[0];
                        string foundationName = result["foundationName"].ToString().Split('^')[0];
                        string footingName = result["footingName"].ToString().Split('^')[0];

                        // Verify element exists in Revit and add (prefix removed)
                        if (FindElementByName(doc, copingName) != null)
                            foundElements.Add($"{pierName} {copingName}");
                        if (FindElementByName(doc, columnName) != null)
                            foundElements.Add($"{pierName} {columnName}");
                        if (FindElementByName(doc, foundationName) != null)
                            foundElements.Add($"{pierName} {foundationName}");
                        if (FindElementByName(doc, footingName) != null)
                            foundElements.Add($"{pierName} {footingName}");
                    }
                }
            }

            // Show message after all pier element combination added to queue
            if (foundElements.Count > 0)
            {
                foundElements = foundElements.Distinct().ToList();
                string elementsList = string.Join(", ", foundElements);
                TaskDialog.Show("Elements Found", $"[{elementsList}]found.");

                TaskDialog.Show("Info", "Starting full combination.");

                if (_actionQueue.Count > 0 && !_isProcessing)
                {
                    UIApplication uiApp = commandData.Application;
                    uiApp.Idling += OnIdling;
                    _isProcessing = true;
                    _lastActionTime = DateTime.Now;
                    LogMessage("Idling event handler registered and full combination started.");
                }
            }
            else
            {
                TaskDialog.Show("Info", "Could not find elements to combine.");
            }
        }

        // Add combination actions to action queue
        private void EnqueueCombineActions(Document doc, SparqlResultSet results, string pierName)
        {
            foreach (SparqlResult result in results)
            {
                string copingName = result["copingName"].ToString().Split('^')[0];
                string columnName = result["columnName"].ToString().Split('^')[0];
                string foundationName = result["foundationName"].ToString().Split('^')[0];
                string footingName = result["footingName"].ToString().Split('^')[0];

                // Capture as local variables
                var localPierName = pierName;
                var localFootingName = footingName;
                var localFoundationName = foundationName;
                var localColumnName = columnName;
                var localCopingName = copingName;

                _actionQueue.Enqueue(() => CombineElements(doc, localFootingName, localFoundationName, $"Combining: Footing -> Foundation ({localPierName})"));
                _actionQueue.Enqueue(() => CombineElements(doc, localFoundationName, localColumnName, $"Combining: Foundation -> Column ({localPierName})"));
                _actionQueue.Enqueue(() => CombineElements(doc, localColumnName, localCopingName, $"Combining: Column -> Coping ({localPierName})"));
            }

            LogMessage($"{pierName} Combination steps for pier added to queue.");
        }

        // Idling event handler
        private void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            if (_actionQueue.Count > 0)
            {
                DateTime currentTime = DateTime.Now;
                if ((currentTime - _lastActionTime) >= _delay)
                {
                    Action action = _actionQueue.Dequeue();
                    try
                    {
                        // Each action runs in a separate transaction
                        UIApplication uiApp = sender as UIApplication;
                        UIDocument uidoc = uiApp.ActiveUIDocument;
                        Document doc = uidoc.Document;

                        using (Transaction trans = new Transaction(doc, "Arrange Pier Elements"))
                        {
                            trans.Start();
                            action.Invoke();
                            trans.Commit();
                        }

                        // Log transaction completion
                        LogMessage("Next element combination completed.");

                        // Update last action time
                        _lastActionTime = DateTime.Now;
                    }
                    catch (Exception ex)
                    {
                        TaskDialog.Show("Error", $"Error occurred during action execution: {ex.Message}");
                        LogMessage($"Error occurred during action execution: {ex.Message}");
                        _actionQueue.Clear(); // Clear queue, stop processing
                        UIApplication uiApp = sender as UIApplication;
                        uiApp.Idling -= OnIdling;
                        _isProcessing = false;
                    }
                }
            }
            else
            {
                // Remove Idling event handler when queue is empty
                UIApplication uiApp = sender as UIApplication;
                uiApp.Idling -= OnIdling;
                _isProcessing = false;
                LogMessage("All Pier elements combination completed.");
                TaskDialog.Show("Info", "All pier element combinations completed.");
            }
        }

        // Partial combination logic
        private Result CombinePartial(Graph g, Document doc)
        {
            // Store results for each pier
            Dictionary<string, SparqlResultSet> pierResults = new Dictionary<string, SparqlResultSet>();

            // Process A1~A7 piers iteratively
            for (int i = 1; i <= 7; i++)
            {
                string pierName = $"A{i}";

                // Generate SPARQL query for each pier
                string query = $@"
PREFIX bso: <https://hyunwoochacha.github.io/SO-ASAS/ontology#>
PREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#>
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>

SELECT ?copingName ?columnName ?foundationName ?footingName WHERE {{
    bso:{pierName}_PierCoping_Instance bso:isAttachedTo bso:{pierName}_PierColumn_Instance .
    bso:{pierName}_PierColumn_Instance bso:isAttachedTo bso:{pierName}_PierFoundation_Instance .
    bso:{pierName}_PierFoundation_Instance bso:isAttachedTo bso:{pierName}_PierFooting_Instance .
    bso:{pierName}_PierCoping_Instance bso:name ?copingName .
    bso:{pierName}_PierColumn_Instance bso:name ?columnName .
    bso:{pierName}_PierFoundation_Instance bso:name ?foundationName .
    bso:{pierName}_PierFooting_Instance bso:name ?footingName .
}}";

                // Execute SPARQL query and store results
                SparqlResultSet results = ExecutePierQuery(g, query, doc, pierName);
                if (results != null && results.Count > 0)
                {
                    pierResults[pierName] = results;
                }
            }

            // Process only piers with results from A1~A7
            if (pierResults.Count > 0)
            {
                // Show selection window for A1~A7
                PierSelectionWindow pierSelectionWindow = new PierSelectionWindow(pierResults.Keys.ToList());
                bool? pierResult = pierSelectionWindow.ShowDialog();

                if (pierResult == true && pierSelectionWindow.SelectedPierName != null)
                {
                    string selectedPier = pierSelectionWindow.SelectedPierName;

                    if (pierResults.ContainsKey(selectedPier))
                    {
                        // Pass selected pier to ProcessPierResults for combination
                        ProcessPierResults(pierResults[selectedPier], doc, selectedPier);
                    }
                    else
                    {
                        TaskDialog.Show("Error", $"{selectedPier}No data for.");
                        return Result.Failed;
                    }
                }
            }
            else
            {
                TaskDialog.Show("Error", "Cannot find data for A1 ~ A7.");
                return Result.Failed;
            }

            return Result.Succeeded;
        }

        // SPARQL query execution function
        private SparqlResultSet ExecutePierQuery(Graph g, string queryPier, Document doc, string pierName)
        {
            try
            {
                return (SparqlResultSet)g.ExecuteQuery(queryPier);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"{pierName} SPARQL query execution failed: {ex.Message}");
                LogMessage($"{pierName} SPARQL query execution failed: {ex.Message}");
                return null;
            }
        }

        private void ProcessPierResultsPartial(SparqlResultSet results, Document doc, string pierName)
        {
            // Partial combination logic:
            // Previously showed CombinationChoiceWindow, now removed.
            // Instead uses ShowCombineDialog(Yes/No) to ask for each element pair.
            foreach (SparqlResult result in results)
            {
                string copingName = result["copingName"].ToString().Split('^')[0];
                string columnName = result["columnName"].ToString().Split('^')[0];
                string foundationName = result["foundationName"].ToString().Split('^')[0];
                string footingName = result["footingName"].ToString().Split('^')[0];

                // Partial Combination: call ShowCombineDialog for each element pair
                if (ShowCombineDialog("Footing", "Foundation"))
                {
                    CombineElementsPartial(doc, footingName, foundationName);
                }

                if (ShowCombineDialog("Foundation", "Column"))
                {
                    CombineElementsPartial(doc, foundationName, columnName);
                }

                if (ShowCombineDialog("Column", "Coping"))
                {
                    CombineElementsPartial(doc, columnName, copingName);
                }
            }
        }

        // Query result processing function
        private void ProcessPierResults(SparqlResultSet results, Document doc, string pierName)
        {
            if (results == null || results.Count == 0)
            {
                TaskDialog.Show("Error", $"{pierName}Cannot find data for.");
                return;
            }

            foreach (SparqlResult result in results)
            {
                string copingName = result["copingName"].ToString().Split('^')[0];
                string columnName = result["columnName"].ToString().Split('^')[0];
                string foundationName = result["foundationName"].ToString().Split('^')[0];
                string footingName = result["footingName"].ToString().Split('^')[0];

                // Ask whether to do full combination
                TaskDialog mainDialog = new TaskDialog($"Pier {pierName} Combination")
                {
                    MainInstruction = $"Pier {pierName}'s elements. Do you want to combine all? ({copingName}, {columnName}, {foundationName}, {footingName})",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
                };

                TaskDialogResult tResult = mainDialog.Show();

                if (tResult == TaskDialogResult.No)
                {
                    // When partial combination
                    if (ShowCombineDialog("Footing", "Foundation"))
                    {
                        _actionQueue.Enqueue(() => CombineElementsPartial(doc, footingName, foundationName));
                    }

                    if (ShowCombineDialog("Foundation", "Column"))
                    {
                        _actionQueue.Enqueue(() => CombineElementsPartial(doc, foundationName, columnName));
                    }

                    if (ShowCombineDialog("Column", "Coping"))
                    {
                        _actionQueue.Enqueue(() => CombineElementsPartial(doc, columnName, copingName));
                    }
                }
                else
                {
                    // Add to action queue even when full combination is selected
                    // Don't execute CombineElementsFull immediately; enqueue and run via OnIdling
                    _actionQueue.Enqueue(() => CombineElementsFull(doc, footingName, foundationName, $"Full Combination: Footing -> Foundation ({pierName})"));
                    _actionQueue.Enqueue(() => CombineElementsFull(doc, foundationName, columnName, $"Full Combination: Foundation -> Column ({pierName})"));
                    _actionQueue.Enqueue(() => CombineElementsFull(doc, columnName, copingName, $"Full Combination: Column -> Coping ({pierName})"));
                }

                // Register Idling event if queue is not empty and not already processing
                if (_actionQueue.Count > 0 && !_isProcessing)
                {
                    UIApplication uiApp = _commandData.Application;
                    uiApp.Idling += OnIdling;
                    _isProcessing = true;
                    _lastActionTime = DateTime.Now;
                    LogMessage("Idling event handler registered for combination.");
                }
            }
        }

        // Partial combination dialog function
        private bool ShowCombineDialog(string elementType1, string elementType2)
        {
            TaskDialog partialDialog = new TaskDialog("Partial Combine");
            partialDialog.MainInstruction = $"{elementType1}and {elementType2}. Do you want to combine?";
            partialDialog.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
            TaskDialogResult tResult = partialDialog.Show();

            return tResult == TaskDialogResult.Yes;
        }

        // Method for partial combination
        private void CombineElementsPartial(Document doc, string elementName1, string elementName2)
        {
            CombineElements(doc, elementName1, elementName2, $"Partial Combination: {elementName1} -> {elementName2}");
        }

        // Method for full combination
        private void CombineElementsFull(Document doc, string elementName1, string elementName2, string actionDescription)
        {
            CombineElements(doc, elementName1, elementName2, actionDescription);
        }

        // Element combination function (queue action for full combination)
        private void CombineElements(Document doc, string elementName1, string elementName2, string actionDescription)
        {
            var element1 = FindElementByName(doc, elementName1);
            var element2 = FindElementByName(doc, elementName2);

            if (element1 == null || element2 == null)
            {
                LogMessage($"Could not find elements: {elementName1}, {elementName2}");
                return;
            }

            // Record detailed calculation
            // DisplayDetailedCalculation(element1, element2, actionDescription); // Commented out - debug dialog disabled

            XYZ element1TopCenter = GetBoundingBoxCenter(element1) + new XYZ(0, 0, (GetHeight(element1) / 2.0) + (GetHeight(element2) / 2.0));
            MoveElementToLocation(doc, element2, element1TopCenter);
            LogMessage($"{actionDescription}: {elementName2} {elementName1} moved upward.");
        }


        // Display detailed calculation process
        private void DisplayDetailedCalculation(Element element1, Element element2, string actionDescription)
        {
            // 1. Extract BoundingBox info
            BoundingBoxXYZ bb1 = element1.get_BoundingBox(null);
            BoundingBoxXYZ bb2 = element2.get_BoundingBox(null);

            // 2. Current coordinate info
            XYZ element1Min = bb1.Min;
            XYZ element1Max = bb1.Max;
            XYZ element1Center = GetBoundingBoxCenter(element1);
            double element1Height = GetHeight(element1);

            XYZ element2Min = bb2.Min;
            XYZ element2Max = bb2.Max;
            XYZ element2Center = GetBoundingBoxCenter(element2);
            double element2Height = GetHeight(element2);

            // 3. Calculate target position
            double targetX = element1Center.X;
            double targetY = element1Center.Y;
            double targetZ = element1Center.Z + (element1Height / 2.0) + (element2Height / 2.0);
            XYZ targetPosition = new XYZ(targetX, targetY, targetZ);

            // 4. Calculate translation vector
            XYZ translationVector = targetPosition - element2Center;

            // 5. Generate detailed info string
            string detailedInfo = $"【{actionDescription}】\n" +
                                 $"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n\n" +
                                 $"■ Step 1: Lower element analysis [{element1.Name}]\n" +
                                 $"  • BoundingBox Min: ({element1Min.X:F3}, {element1Min.Y:F3}, {element1Min.Z:F3})\n" +
                                 $"  • BoundingBox Max: ({element1Max.X:F3}, {element1Max.Y:F3}, {element1Max.Z:F3})\n" +
                                 $"  • Center point calculation: \n" +
                                 $"    X = (Min.X + Max.X) / 2 = ({element1Min.X:F3} + {element1Max.X:F3}) / 2 = {element1Center.X:F3}\n" +
                                 $"    Y = (Min.Y + Max.Y) / 2 = ({element1Min.Y:F3} + {element1Max.Y:F3}) / 2 = {element1Center.Y:F3}\n" +
                                 $"    Z = (Min.Z + Max.Z) / 2 = ({element1Min.Z:F3} + {element1Max.Z:F3}) / 2 = {element1Center.Z:F3}\n" +
                                 $"  • Height: Max.Z - Min.Z = {element1Max.Z:F3} - {element1Min.Z:F3} = {element1Height:F3}\n\n" +

                                 $"■ Step 2: Upper element analysis [{element2.Name}]\n" +
                                 $"  • BoundingBox Min: ({element2Min.X:F3}, {element2Min.Y:F3}, {element2Min.Z:F3})\n" +
                                 $"  • BoundingBox Max: ({element2Max.X:F3}, {element2Max.Y:F3}, {element2Max.Z:F3})\n" +
                                 $"  • Current center point: ({element2Center.X:F3}, {element2Center.Y:F3}, {element2Center.Z:F3})\n" +
                                 $"  • Height: {element2Height:F3}\n\n" +

                                 $"■ Step 3: Target position calculation\n" +
                                 $"  • X coordinate = Lower element center X = {targetX:F3}\n" +
                                 $"  • Y coordinate = Lower element center Y = {targetY:F3}\n" +
                                 $"  • Z coordinate calculation:\n" +
                                 $"    = Lower center Z + (Lower height/2) + (Upper height/2)\n" +
                                 $"    = {element1Center.Z:F3} + ({element1Height:F3}/2) + ({element2Height:F3}/2)\n" +
                                 $"    = {element1Center.Z:F3} + {element1Height / 2:F3} + {element2Height / 2:F3}\n" +
                                 $"    = {targetZ:F3}\n\n" +

                                 $"■ Step 4: Move vector calculation\n" +
                                 $"  • ΔX = TargetX - CurrentX = {targetX:F3} - {element2Center.X:F3} = {translationVector.X:F3}\n" +
                                 $"  • ΔY = TargetY - CurrentY = {targetY:F3} - {element2Center.Y:F3} = {translationVector.Y:F3}\n" +
                                 $"  • ΔZ = TargetZ - CurrentZ = {targetZ:F3} - {element2Center.Z:F3} = {translationVector.Z:F3}\n" +
                                 $"  • Total translation distance = √(ΔX² + ΔY² + ΔZ²) = {translationVector.GetLength():F3}\n\n" +

                                 $"■ Step 5: Final position\n" +
                                 $"  • Upper element new center point: ({targetPosition.X:F3}, {targetPosition.Y:F3}, {targetPosition.Z:F3})\n" +
                                 $"  • Upper element new Min: ({element2Min.X + translationVector.X:F3}, {element2Min.Y + translationVector.Y:F3}, {element2Min.Z + translationVector.Z:F3})\n" +
                                 $"  • Upper element new Max: ({element2Max.X + translationVector.X:F3}, {element2Max.Y + translationVector.Y:F3}, {element2Max.Z + translationVector.Z:F3})\n" +
                                 $"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━";

            // Display via TaskDialog
            TaskDialog detailDialog = new TaskDialog("Bridge Element Combination Calculation Details")
            {
                MainInstruction = actionDescription,
                MainContent = detailedInfo,
                CommonButtons = TaskDialogCommonButtons.Ok,
                DefaultButton = TaskDialogResult.Ok,
                ExpandedContent = $"【Additional Information】\n" +
                                 $"• Element1 ID: {element1.Id}\n" +
                                 $"• Element2 ID: {element2.Id}\n" +
                                 $"• Category: {element1.Category.Name}\n" +
                                 $"• Execution time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
            };

            detailDialog.Show();

            // Also record in log file
            LogMessage("=== Calculation Details ===");
            LogMessage(detailedInfo);
        }

        // Show summary after full pier combination
        private void ShowCompletionSummary(Document doc, string pierName)
        {
            // Find all elements for the pier
            var pierElements = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .Where(e => e.Name.Contains(pierName))
                .ToList();

            string summary = $"【{pierName} Pier Combination Summary】\n\n";
            summary += "■ Final Placement Result:\n";

            foreach (var element in pierElements.OrderBy(e => GetBoundingBoxCenter(e).Z))
            {
                BoundingBoxXYZ bb = element.get_BoundingBox(null);
                if (bb != null)
                {
                    XYZ center = GetBoundingBoxCenter(element);
                    summary += $"\n• {element.Name}\n";
                    summary += $"  Position: ({center.X:F2}, {center.Y:F2}, {center.Z:F2})\n";
                    summary += $"  Height range: Z = {bb.Min.Z:F2} ~ {bb.Max.Z:F2}\n";
                }
            }

            summary += $"\n■ Verification:\n";
            summary += $"• Total {pierElements.Count}elements combined\n";
            summary += $"• Vertical alignment check: ";

            // Check if all X, Y coordinates are identical
            var centers = pierElements.Select(e => GetBoundingBoxCenter(e)).ToList();
            bool aligned = centers.All(c => Math.Abs(c.X - centers[0].X) < 0.001 &&
                                           Math.Abs(c.Y - centers[0].Y) < 0.001);
            summary += aligned ? "Normal ✓" : "Error occurred ✗";

            TaskDialog.Show($"{pierName} Pier Combination Complete", summary);
        }


        // Find Revit element by name
        private Element FindElementByName(Document doc, string name)
        {
            var collector = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .ToElements();

            foreach (var element in collector)
            {
                if (element.Name.Trim().Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return element;
                }
            }

            LogMessage($"Could not find element: {name}");
            return null;
        }

        // Get element center
        private XYZ GetBoundingBoxCenter(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return (boundingBox.Min + boundingBox.Max) / 2.0;
        }

        // Get element max point
        private XYZ GetBoundingBoxMaxPoint(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return boundingBox.Max;
        }

        // Get element height
        private double GetHeight(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return boundingBox.Max.Z - boundingBox.Min.Z;
        }

        // Get element center (additional)
        private XYZ GetElementCenter(Element element)
        {
            return GetBoundingBoxCenter(element);
        }

        // Move element function
        private void MoveElementToLocation(Document doc, Element element, XYZ newLocation)
        {
            XYZ currentCenter = GetBoundingBoxCenter(element);
            XYZ translation = newLocation - currentCenter;
            ElementTransformUtils.MoveElement(doc, element.Id, translation);
            LogMessage($"Moving {element.Name} to {newLocation}");
        }

        // Log message function
        private void LogMessage(string message)
        {
            try
            {
                File.AppendAllText(_logFilePath, $"[{DateTime.Now}] {message}\n");
            }
            catch (Exception ex)
            {
                // Alert user if unable to write to log file
                TaskDialog.Show("Error", $"Cannot record log file: {ex.Message}");
            }
        }
    }



    [Transaction(TransactionMode.Manual)]
    public class ArrangeAbutmentElements : IExternalCommand
    {
        private string _ttlFilePath;
        private ExternalCommandData _commandData;
        private string _message;
        private ElementSet _elements;
        private string _selectedAbutment;

        // Static members for action queue and state management
        private static Queue<Action> _actionQueue = new Queue<Action>();
        private static bool _isProcessing = false;
        private static DateTime _lastActionTime = DateTime.MinValue;
        private static readonly TimeSpan _delay = TimeSpan.FromMilliseconds(500); // 1 second delay

        public ArrangeAbutmentElements(string ttlFilePath)
        {
            _ttlFilePath = ttlFilePath;
        }

        public ArrangeAbutmentElements(string ttlFilePath, string selectedAbutment)
        {
            _ttlFilePath = ttlFilePath;
            _selectedAbutment = selectedAbutment;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Reset static state from previous runs
            _actionQueue.Clear();
            _isProcessing = false;

            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            Graph g = new Graph();
            try
            {
                FileLoader.Load(g, _ttlFilePath);
            }
            catch (Exception ex)
            {
                message = $"Ontology file loading failed: {ex.Message}";
                return Result.Failed;
            }

            // Execute logic based on _selectedAbutment without UI
            if (string.IsNullOrEmpty(_selectedAbutment))
            {
                // Full combination logic
                CombineAllElements(doc, g);
            }
            else
            {
                // Partial combination: _selectedAbutment is "A1" or "A2"
                CombineAbutment(doc, g, _selectedAbutment);
            }

            return Result.Succeeded;
        }

        public void CombineAllElements(Document doc, Graph g)
        {
            // A1 Combination
            CombineAbutment(doc, g, "A1");

            // A2 Combination
            CombineAbutment(doc, g, "A2");

        }

        public void CombineAbutment(Document doc, Graph g, string abutmentId)
        {
            string query = abutmentId == "A1" ? @"
    PREFIX bso: <https://hyunwoochacha.github.io/SO-ASAS/ontology#>
    SELECT ?footingName ?foundationName ?wallName ?wingLeftName ?wingRightName ?capName WHERE {
        bso:A1_AbutmentFooting_Instance bso:name ?footingName .
        bso:A1_AbutmentFoundation_Instance bso:name ?foundationName .
        bso:A1_AbutmentWall_Instance bso:name ?wallName .
        bso:A1_AbutmentWingWall_Left_Instance bso:name ?wingLeftName .
        bso:A1_AbutmentWingWall_Right_Instance bso:name ?wingRightName .
        OPTIONAL {
         bso:A1_AbutmentCap_Instance bso:name ?capName .
        }
    }" : @"
    PREFIX bso: <https://hyunwoochacha.github.io/SO-ASAS/ontology#>
    SELECT ?footingName ?foundationName ?wallName ?wingLeftName ?wingRightName ?capName WHERE {
        bso:A2_AbutmentFooting_Instance bso:name ?footingName .
        bso:A2_AbutmentFoundation_Instance bso:name ?foundationName .
        bso:A2_AbutmentWall_Instance bso:name ?wallName .
        bso:A2_AbutmentWingWall_Left_Instance bso:name ?wingLeftName .
        bso:A2_AbutmentWingWall_Right_Instance bso:name ?wingRightName .
        OPTIONAL {
         bso:A2_AbutmentCap_Instance bso:name ?capName .
        }
    }";

            SparqlResultSet results = (SparqlResultSet)g.ExecuteQuery(query);
            if (results == null || results.Count == 0)
            {
                MessageBox.Show($"{abutmentId} Query failed or no results");
                return;
            }

            // Collect SPARQL results
            List<string> foundElements = new List<string>();
            foreach (SparqlResult r in results)
            {
                // Required 5 elements
                Add(foundElements, r, "footingName");
                Add(foundElements, r, "foundationName");
                Add(foundElements, r, "wallName");
                Add(foundElements, r, "wingLeftName");
                Add(foundElements, r, "wingRightName");

                // Optional (Cap)
                Add(foundElements, r, "capName");   // Skips if no value
            }

            // Display results in UI
            MessageBox.Show($"[{string.Join(", ", foundElements)}] found.");

            // Add combination tasks to queue (processed sequentially)
            foreach (SparqlResult result in results)
            {
                // Convert element names to Revit Elements
                Element footing = FindElementByName(doc, result["footingName"].ToString().Split('^')[0]);
                Element foundation = FindElementByName(doc, result["foundationName"].ToString().Split('^')[0]);
                Element wall = FindElementByName(doc, result["wallName"].ToString().Split('^')[0]);
                Element wingLeft = FindElementByName(doc, result["wingLeftName"].ToString().Split('^')[0]);
                Element wingRight = FindElementByName(doc, result["wingRightName"].ToString().Split('^')[0]);
                Element cap = null;
                if (result.HasValue("capName") && result["capName"] != null)
                    cap = FindElementByName(doc, result["capName"].ToString().Split('^')[0]);
                // Add combination tasks sequentially for each element
                if (footing != null)
                {
                    CombineElements(doc, footing, foundation, wall, wingLeft, wingRight, cap, abutmentId, "Footing");
                }
                if (foundation != null)
                {
                    CombineElements(doc, footing, foundation, wall, wingLeft, wingRight, cap, abutmentId, "Foundation");
                }
                if (wall != null)
                {
                    CombineElements(doc, footing, foundation, wall, wingLeft, wingRight, cap, abutmentId, "Wall");
                }
                if (wingLeft != null)
                {
                    CombineElements(doc, footing, foundation, wall, wingLeft, wingRight, cap, abutmentId, "Left Wing Wall");
                }
                if (wingRight != null)
                {
                    CombineElements(doc, footing, foundation, wall, wingLeft, wingRight, cap, abutmentId, "Right Wing Wall");
                }
                if (cap != null)
                {
                    CombineElements(doc, footing, foundation, wall, wingLeft, wingRight, cap, abutmentId, "Abutment Cap");
                }
            }

            // Start queue execution via OnIdling
            if (!_isProcessing)
            {
                _isProcessing = true;
                UIApplication uiApp = new UIApplication(doc.Application);
                uiApp.Idling += OnIdling;
            }
        }





        // CombineElements method
        private void CombineElements(Document doc, Element footing, Element foundation, Element wall, Element wingLeft, Element wingRight, Element cap, string abutmentId, string elementName)
        {
            // Add element combination tasks to queue
            if (footing != null)
            {
                _actionQueue.Enqueue(() =>
                {
                    using (Transaction trans = new Transaction(doc))
                    {
                        trans.Start($"Arrange Abutment Elements {abutmentId} - {elementName}");

                        // Footing combination
                        MoveElementToLocation(footing, GetElementCenter(footing));  // Example move operation
                        trans.Commit();
                    }

                });
            }

            if (foundation != null)
            {
                _actionQueue.Enqueue(() =>
                {
                    using (Transaction trans = new Transaction(doc))
                    {
                        trans.Start($"Arrange Abutment Elements {abutmentId} - {elementName}");

                        // Foundation combination
                        XYZ foundationLocation = footing != null
                            ? new XYZ(GetElementCenter(footing).X, GetElementCenter(footing).Y, GetBoundingBoxMaxPoint(footing).Z + GetHeight(foundation) / 2)
                            : GetElementCenter(foundation);
                        MoveElementToLocation(foundation, foundationLocation);
                        trans.Commit();
                    }

                });
            }

            if (wall != null)
            {
                _actionQueue.Enqueue(() =>
                {
                    using (Transaction trans = new Transaction(doc))
                    {
                        trans.Start($"Arrange Abutment Elements {abutmentId} - {elementName}");

                        // Wall combination
                        MoveWallAndWingsToFoundationTop(doc, foundation, wall, wingLeft, wingRight, abutmentId);
                        trans.Commit();
                    }

                });
            }

            // Left/right wing walls and abutment cap can be added similarly
            // Enqueue additional combination tasks
            // Start Idling event after all combination tasks are added
            if (!_isProcessing)
            {
                _isProcessing = true;
                UIApplication uiApp = new UIApplication(doc.Application);
                uiApp.Idling += OnIdling;
            }
        }

        // OnIdling method
        private void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            DateTime currentTime = DateTime.Now;

            if (_actionQueue.Count > 0 && (currentTime - _lastActionTime) >= _delay)
            {
                Action action = _actionQueue.Dequeue();
                action.Invoke();
                _lastActionTime = DateTime.Now;
            }
            else if (_actionQueue.Count == 0)
            {
                UIApplication uiApp = sender as UIApplication;
                uiApp.Idling -= OnIdling;
                _isProcessing = false;
                MessageBox.Show("All combination work completed.");
            }
        }

        void Add(List<string> list, SparqlResult r, string varName)
        {
            if (r.HasValue(varName) && r[varName] != null)
                list.Add(r[varName].ToString().Split('^')[0]);
        }



        private void MoveWallAndWingsToFoundationTop(Document doc, Element foundation, Element wall, Element wingLeft, Element wingRight, string abutmentId)
        {
            // 1. Move wall above foundation
            XYZ translation;
            if (abutmentId == "A1")
            {
                // A1: Move wall to left edge of foundation
                XYZ foundationTopLeft = new XYZ(GetBoundingBoxMaxPoint(foundation).X, GetBoundingBoxMinPoint(foundation).Y, GetBoundingBoxMaxPoint(foundation).Z);
                XYZ wallBottomLeft = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                translation = foundationTopLeft - wallBottomLeft;

                // Get wall base length
                double wallLength = GetBoundingBoxMaxPoint(wall).X - GetBoundingBoxMinPoint(wall).X;

                // Move wall right by wall length
                translation = foundationTopLeft - wallBottomLeft - new XYZ(wallLength * 2 / 5, 0, 0);
            }
            else if (abutmentId == "A2")
            {
                // A2: Keep centered as per original code
                XYZ foundationTopCenter = new XYZ(GetBoundingBoxMinPoint(foundation).X, GetBoundingBoxMinPoint(foundation).Y, GetBoundingBoxMaxPoint(foundation).Z);
                XYZ wallBottomCenter = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                translation = foundationTopCenter - wallBottomCenter;
            }
            else
            {
                // Default: center placement (for abutmentId other than A1, A2)
                XYZ foundationTopCenter = new XYZ(GetBoundingBoxMinPoint(foundation).X, GetBoundingBoxMinPoint(foundation).Y, GetBoundingBoxMaxPoint(foundation).Z);
                XYZ wallBottomCenter = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                translation = foundationTopCenter - wallBottomCenter;
            }
            MoveElementToLocation(wall, GetElementCenter(wall) + translation);

            // 2. Align left wing wall to wall
            if (wingLeft != null)
            {
                XYZ wingLeftTranslation = CalculateTranslationForLeftWing(wall, wingLeft, abutmentId);
                MoveElementToLocation(wingLeft, GetElementCenter(wingLeft) + wingLeftTranslation);
            }

            // 3. Align right wing wall to wall
            if (wingRight != null)
            {
                XYZ wingRightTranslation = CalculateTranslationForRightWing(wall, wingRight, abutmentId);
                MoveElementToLocation(wingRight, GetElementCenter(wingRight) + wingRightTranslation);
            }
        }

        private XYZ CalculateTranslationForLeftWing(Element wall, Element wingLeft, string abutmentId)
        {
            XYZ wallCorner, wingLeftCorner;

            if (abutmentId == "A1")
            {
                // A1 case: calculate left corner alignment
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMaxPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingLeftCorner = new XYZ(GetBoundingBoxMinPoint(wingLeft).X, GetBoundingBoxMaxPoint(wingLeft).Y, GetBoundingBoxMinPoint(wingLeft).Z);

                // Get wing wall base length
                double wingLength = GetBoundingBoxMaxPoint(wingLeft).X - GetBoundingBoxMinPoint(wingLeft).X;
                // Get wall base length
                double wallLength = GetBoundingBoxMaxPoint(wall).X - GetBoundingBoxMinPoint(wall).X;

                // Calculate vector to move to left edge of wall
                return wallCorner - wingLeftCorner - new XYZ(wingLength, 0, 0) + new XYZ(wallLength * 2 / 5, 0, 0);

            }
            else
            {
                // A2 case: align left wing wall to left edge of wall
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMaxPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingLeftCorner = new XYZ(GetBoundingBoxMinPoint(wingLeft).X, GetBoundingBoxMaxPoint(wingLeft).Y, GetBoundingBoxMinPoint(wingLeft).Z);

                // Calculate vector to move to left edge of wall
                return wallCorner - wingLeftCorner;
            }
        }

        private XYZ CalculateTranslationForRightWing(Element wall, Element wingRight, string abutmentId)
        {
            XYZ wallCorner, wingRightCorner;

            if (abutmentId == "A1")
            {
                // A1 case: calculate right corner alignment
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingRightCorner = new XYZ(GetBoundingBoxMinPoint(wingRight).X, GetBoundingBoxMinPoint(wingRight).Y, GetBoundingBoxMinPoint(wingRight).Z);

                // Get wall base length
                double wingLength = GetBoundingBoxMaxPoint(wingRight).X - GetBoundingBoxMinPoint(wingRight).X;

                // Get wall base length
                double wallLength = GetBoundingBoxMaxPoint(wall).X - GetBoundingBoxMinPoint(wall).X;

                // Calculate vector to move to right edge of wall
                return wallCorner - wingRightCorner - new XYZ(wingLength, 0, 0) + new XYZ(wallLength * 2 / 5, 0, 0);
            }
            else
            {
                // A2 case: align right wing wall to right edge of wall
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingRightCorner = new XYZ(GetBoundingBoxMinPoint(wingRight).X, GetBoundingBoxMinPoint(wingRight).Y, GetBoundingBoxMinPoint(wingRight).Z);
            }

            // Calculate vector to move to left edge of wall
            return wallCorner - wingRightCorner;
        }

        private Element FindElementByName(Document doc, string name)
        {
            var elements = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType().ToElements();
            foreach (Element element in elements)
            {
                if (element.Name.Equals(name))
                {
                    return element;
                }
            }
            return null;
        }

        private XYZ GetBoundingBoxMinPoint(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return boundingBox.Min;
        }

        private XYZ GetBoundingBoxMaxPoint(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return boundingBox.Max;
        }

        private double GetHeight(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return boundingBox.Max.Z - boundingBox.Min.Z;
        }

        private void MoveElementToLocation(Element element, XYZ newLocation)
        {
            XYZ elementCenter = GetElementCenter(element);
            XYZ translation = newLocation - elementCenter;
            ElementTransformUtils.MoveElement(element.Document, element.Id, translation);
        }

        private XYZ GetElementCenter(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return (boundingBox.Min + boundingBox.Max) / 2.0;
        }

        // Additional methods
        private XYZ GetTopLeftCorner(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            if (boundingBox == null)
            {
                throw new InvalidOperationException("Bounding box is null.");
            }
            return new XYZ(boundingBox.Min.X, boundingBox.Max.Y, boundingBox.Max.Z);
        }

        private XYZ GetTopRightCorner(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            if (boundingBox == null)
            {
                throw new InvalidOperationException("Bounding box is null.");
            }
            return new XYZ(boundingBox.Max.X, boundingBox.Max.Y, boundingBox.Max.Z);
        }

        private void MoveElementToTopLeftCorner(Element element, XYZ newTopLeftCorner)
        {
            XYZ currentTopLeftCorner = GetTopLeftCorner(element);
            XYZ translation = newTopLeftCorner - currentTopLeftCorner;
            ElementTransformUtils.MoveElement(element.Document, element.Id, translation);
        }

        private void MoveElementToTopRightCorner(Element element, XYZ newTopRightCorner)
        {
            XYZ currentTopRightCorner = GetTopRightCorner(element);
            XYZ translation = newTopRightCorner - currentTopRightCorner;
            ElementTransformUtils.MoveElement(element.Document, element.Id, translation);
        }

        public XYZ GetWallBottomRightCorner(BoundingBoxXYZ boundingBox)
        {
            return new XYZ(boundingBox.Max.X, boundingBox.Min.Y, boundingBox.Min.Z);
        }

        public XYZ GetWingLeftBottomLeftCorner(BoundingBoxXYZ boundingBox)
        {
            return new XYZ(boundingBox.Min.X, boundingBox.Min.Y, boundingBox.Min.Z);
        }

        public double CalculateDistance(XYZ point1, XYZ point2)
        {
            return point1.DistanceTo(point2);
        }


    }


    [Transaction(TransactionMode.Manual)]
    public class ArrangeSuperStructureElements : IExternalCommand
    {
        private string _ttlFilePath;
        // Static members for action queue and state management
        private static Queue<Action> _actionQueue = new Queue<Action>();
        private static bool _isProcessing = false;
        private static DateTime _lastActionTime = DateTime.MinValue;
        private static readonly TimeSpan _delay = TimeSpan.FromMilliseconds(500); // 0.5 second delay

        public ArrangeSuperStructureElements(string ttlFilePath)
        {
            _ttlFilePath = @"C:\Users\chw42\source\repos\RevitBridge2\RevitBridge2\bin\Debug\WBS_Bridge.ttl";
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            // Load ontology file
            Graph g = new Graph();
            try
            {
                FileLoader.Load(g, _ttlFilePath);
                TaskDialog.Show("Info", "Ontology file loaded successfully");
            }
            catch (Exception ex)
            {
                message = "SPARQL query failed or no results.";
                TaskDialog.Show("Error", message);
                return Result.Failed;
            }




            SparqlResultSet results = ExecuteSuperStructureQuery(g);
            if (results == null || results.Count == 0)
            {
                message = "SPARQL query failed or no results.";
                TaskDialog.Show("Error", message);
                return Result.Failed;
            }

            TaskDialog.Show("Info", $"SPARQL query result count: {results.Count}");

            // List to store found elements
            List<string> foundElements = new List<string>();


            EnqueueCombineActions(doc, results);

            // Register Idling event and start processing
            if (!_isProcessing && _actionQueue.Count > 0)
            {
                UIApplication uiApp = commandData.Application;
                uiApp.Idling += OnIdling;
                _isProcessing = true;
                _lastActionTime = DateTime.Now;
            }

            return Result.Succeeded;

        }

        private SparqlResultSet ExecuteSuperStructureQuery(Graph g)
        {
            string query = @"
            PREFIX bso: <https://hyunwoochacha.github.io/SO-ASAS/ontology#>
            PREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#>
            PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>

            SELECT ?slabName ?protectivewallLeftName ?protectivewallRightName WHERE {
            bso:Protectivewall_Left_Instance bso:isPutOn bso:Slab_Instance .
            bso:Protectivewall_Right_Instance bso:isPutOn bso:Slab_Instance .
            bso:Slab_Instance bso:name ?slabName .
            bso:Protectivewall_Left_Instance bso:name ?protectivewallLeftName .
            bso:Protectivewall_Right_Instance bso:name ?protectivewallRightName .
            }";

            try
            {
                return (SparqlResultSet)g.ExecuteQuery(query);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"SPARQL query execution failed: {ex.Message}");
                return null;
            }
        }

        private void EnqueueCombineActions(Document doc, SparqlResultSet results)
        {
            foreach (SparqlResult result in results)
            {
                string slabName = result["slabName"].ToString().Split('^')[0];
                string protectivewallLeftName = result["protectivewallLeftName"].ToString().Split('^')[0];
                string protectivewallRightName = result["protectivewallRightName"].ToString().Split('^')[0];

                // Enqueue individual protective wall move actions
                _actionQueue.Enqueue(() => MoveProtectivewallLeft(doc, slabName, protectivewallLeftName));
                _actionQueue.Enqueue(() => MoveProtectivewallRight(doc, slabName, protectivewallRightName));
            }
        }

        private void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            if (_actionQueue.Count > 0)
            {
                DateTime currentTime = DateTime.Now;
                if ((currentTime - _lastActionTime) >= _delay)
                {
                    Action action = _actionQueue.Dequeue();
                    try
                    {
                        // Each action runs in a separate transaction
                        UIApplication uiApp = sender as UIApplication;
                        UIDocument uidoc = uiApp.ActiveUIDocument;
                        Document doc = uidoc.Document;

                        using (Transaction trans = new Transaction(doc, "Arrange SuperStructure Elements"))
                        {
                            trans.Start();
                            action.Invoke();
                            trans.Commit();
                        }

                        // Update last action time
                        _lastActionTime = DateTime.Now;
                    }
                    catch (Exception ex)
                    {
                        TaskDialog.Show("Error", $"Error occurred during action execution: {ex.Message}");
                        _actionQueue.Clear(); // Clear queue, stop processing
                        UIApplication uiApp = sender as UIApplication;
                        uiApp.Idling -= OnIdling;
                        _isProcessing = false;
                    }
                }
            }
            else
            {
                // Remove Idling event handler when queue is empty
                UIApplication uiApp = sender as UIApplication;
                uiApp.Idling -= OnIdling;
                _isProcessing = false;
                TaskDialog.Show("Info", "Upper structure element combination completed.");
            }
        }
        private void MoveProtectivewallLeft(Document doc, string slabName, string protectivewallLeftName)
        {
            var slab = FindElementByName(doc, slabName);
            var protectivewallLeft = FindElementByName(doc, protectivewallLeftName);

            if (slab == null || protectivewallLeft == null)
            {
                // Error handling
                return;
            }

            XYZ slabMinPoint = GetBoundingBoxMinPoint(slab);
            XYZ slabMaxPoint = GetBoundingBoxMaxPoint(slab);

            // Calculate slab X-axis center
            double slabCenterX = (slabMinPoint.X + slabMaxPoint.X) / 2.0;
            // Place left protective wall at slab left edge
            double zPositionLeft = GetZAtPosition(slab, slabCenterX, slabMinPoint.Y + (slabMaxPoint.Y - slabMinPoint.Y) / 2.0);

            // Calculate left protective wall position
            XYZ protectivewallLeftLocation = new XYZ(
                slabCenterX,
                slabMinPoint.Y + (slabMaxPoint.Y - slabMinPoint.Y) / 2.0, // Slab min Y (left edge)
                zPositionLeft
            );

            MoveElementToLocation(doc, protectivewallLeft, protectivewallLeftLocation);
            SetElementHeightToZero(doc, protectivewallLeft);
        }

        private void MoveProtectivewallRight(Document doc,
                                     string slabName,
                                     string protectivewallRightName)
        {
            var slab = FindElementByName(doc, slabName);
            var protectiveRight = FindElementByName(doc, protectivewallRightName);
            if (slab == null || protectiveRight == null) return;

            // ───── Slab position and size ───────────────────────────────
            XYZ slabMin = GetBoundingBoxMinPoint(slab);
            XYZ slabMax = GetBoundingBoxMaxPoint(slab);
            double slabMidX = (slabMin.X + slabMax.X) * 0.5;
            double slabMidY = (slabMin.Y + slabMax.Y) * 0.5;
            double slabTopZ = slabMax.Z;                   // Slab top Z
            double slabThick = GetHeight(slab);             // = Height variable

            // ───── Protective wall half height (for center position calculation) ─────────────
            double wallHalfH = GetHeight(protectiveRight) * 0.5;

            // ───── Target coordinates ─────────────────────────────────────
            XYZ target = new XYZ(
                slabMidX,              // X = Slab center
                slabMidY,              // Y = Slab center (use slabMin.Y or slabMax.Y for left/right)
                slabTopZ + wallHalfH   // Z = Slab top + protective wall half height
            );

            MoveElementToLocation(doc, protectiveRight, target);

            // ───── Step 2: Project precisely onto slab top face ────────────────
            View3D v3d = GetAnyNonTemplate3DView(doc);

            ProjectDownOntoSlab(doc, protectiveRight, slab, v3d);
            SetElementHeightToZero(doc, protectiveRight);
        }




        // (B) ===== ProjectDownOntoSlab =====
        private void ProjectDownOntoSlab(Document doc,
                                 Element wall,
                                 Element slab,
                                 View3D view3d)
        {
            // ---------- (a) Ray start point ----------
            XYZ start = GetTopCenterPoint(wall) + XYZ.BasisZ * UnitUtils.ConvertToInternalUnits(100, UnitTypeId.Millimeters);
            XYZ dir = XYZ.BasisZ.Negate();     // (0,0,-1)

            // ---------- (b) Filter: only Floor like slab ------------
            ElementFilter floorFilter = new ElementCategoryFilter(BuiltInCategory.OST_Floors);

            ReferenceIntersector inter =
                new ReferenceIntersector(floorFilter, FindReferenceTarget.Element, view3d);

            ReferenceWithContext ctx = inter.FindNearest(start, dir);
            if (ctx == null) return;            // No intersection found

            // Check Id in case it hit a different Floor
            if (ctx.GetReference().ElementId != slab.Id) return;

            double dz = ctx.Proximity;          // Distance from start to slab top
            if (dz <= UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters)) return;

            ElementTransformUtils.MoveElement(doc, wall.Id, dir.Multiply(dz));
        }

        // ──────────────────────────────────────────────────────────────
        // Helper functions
        private View3D GetAnyNonTemplate3DView(Document doc)
        {
            return new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D))
                    .Cast<View3D>()
                    .First(v => !v.IsTemplate);
        }


        private void CombineElements(Document doc, string slabName, string protectivewallLeftName, string protectivewallRightName)
        {
            var slab = FindElementByName(doc, slabName);
            var protectivewallLeft = FindElementByName(doc, protectivewallLeftName);
            var protectivewallRight = FindElementByName(doc, protectivewallRightName);

            if (slab == null)
            {
                TaskDialog.Show("Error", $"Cannot find slab: {slabName}");
                return;
            }

            XYZ slabMinPoint = GetBoundingBoxMinPoint(slab);
            XYZ slabMaxPoint = GetBoundingBoxMaxPoint(slab);

            // Calculate slab Y-axis center
            double slabCenterX = (slabMinPoint.X + slabMaxPoint.X) / 2.0;

            if (protectivewallLeft != null)
            {
                // Place left protective wall at slab left edge (X min)
                double zPositionLeft = GetZAtPosition(slab, slabCenterX, slabMinPoint.Y + (slabMaxPoint.Y - slabMinPoint.Y) / 2.0);

                XYZ protectivewallLeftLocation = new XYZ(
                    slabCenterX, // Slab min X (left edge)
                    slabMinPoint.Y + (slabMaxPoint.Y - slabMinPoint.Y) / 2.0,
                    zPositionLeft
                );
                MoveElementToLocation(doc, protectivewallLeft, protectivewallLeftLocation);
                SetElementHeightToZero(doc, protectivewallLeft);
            }

            if (protectivewallRight != null)
            {
                // Position right protective wall at slab right edge
                double zPositionRight = GetZAtPosition(slab, slabCenterX, slabMaxPoint.Y - (slabMaxPoint.Y - slabMinPoint.Y) / 2.0);
                // Place right protective wall at slab right edge (X max)
                XYZ protectivewallRightLocation = new XYZ(
                    slabCenterX,
                    slabMaxPoint.Y - (slabMaxPoint.Y - slabMinPoint.Y) / 2.0,
                    zPositionRight
                );
                MoveElementToLocation(doc, protectivewallRight, protectivewallRightLocation);
                SetElementHeightToZero(doc, protectivewallRight);
            }
        }


        // Insert anywhere in class (next to other utility functions)
        private XYZ GetTopCenterPoint(Element el)
        {
            BoundingBoxXYZ bb = el.get_BoundingBox(null);
            if (bb == null) throw new InvalidOperationException("BoundingBox null");
            return new XYZ(
                (bb.Min.X + bb.Max.X) * 0.5,   // X center
                (bb.Min.Y + bb.Max.Y) * 0.5,   // Y center
                bb.Max.Z                       // Top Z
            );
        }
        private XYZ GetBottomCenterPoint(Element el)
        {
            BoundingBoxXYZ bb = el.get_BoundingBox(null);
            return new XYZ(
                (bb.Min.X + bb.Max.X) * 0.5,
                (bb.Min.Y + bb.Max.Y) * 0.5,
                bb.Min.Z);
        }





        //private void CombineElements(Document doc, string slabName, string protectivewallLeftName, string protectivewallRightName)
        //{
        //    using (Transaction trans = new Transaction(doc))
        //    {
        //        trans.Start("Arrange SuperStructure Elements");

        //        var slab = FindElementByName(doc, slabName);
        //        var protectivewallLeft = FindElementByName(doc, protectivewallLeftName);
        //        var protectivewallRight = FindElementByName(doc, protectivewallRightName);

        //        if (slab != null)
        //        {
        //            // Calculate slab position
        //            XYZ slabMinPoint = GetBoundingBoxMinPoint(slab);
        //            XYZ slabMaxPoint = GetBoundingBoxMaxPoint(slab);

        //            // Calculate slab X-axis center
        //            double slabCenterX = (slabMinPoint.X + slabMaxPoint.X) / 2.0;

        //            if (protectivewallLeft != null)
        //            {
        //                // Position left protective wall at slab left Y edge and X center
        //                double zPositionLeft = GetZAtPosition(slab, slabCenterX, slabMinPoint.Y + (slabMaxPoint.Y - slabMinPoint.Y) / 2.0);
        //                XYZ protectivewallLeftLocation = new XYZ(
        //                    slabCenterX,
        //                    slabMinPoint.Y + (slabMaxPoint.Y - slabMinPoint.Y) / 2.0,
        //                    zPositionLeft
        //                );
        //                MoveElementToLocation(doc, protectivewallLeft, protectivewallLeftLocation);
        //                SetElementHeightToZero(doc, protectivewallLeft);
        //            }

        //            if (protectivewallRight != null)
        //            {
        //                // Position right protective wall at slab right edge
        //                double zPositionRight = GetZAtPosition(slab, slabCenterX, slabMaxPoint.Y - (slabMaxPoint.Y - slabMinPoint.Y) / 2.0);
        //                XYZ protectivewallRightLocation = new XYZ(
        //                    slabCenterX,
        //                    slabMaxPoint.Y - (slabMaxPoint.Y - slabMinPoint.Y) / 2.0,
        //                    zPositionRight
        //                );
        //                MoveElementToLocation(doc, protectivewallRight, protectivewallRightLocation);
        //                SetElementHeightToZero(doc, protectivewallRight);
        //            }
        //        }

        //        trans.Commit();
        //    }
        //}

        private void SetElementHeightToZero(Document doc, Element element)
        {
            // Set "Height Offset from Level" parameter to 0
            Parameter heightParam = element.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
            if (heightParam != null && !heightParam.IsReadOnly)
            {
                heightParam.Set(0.0);
                //TaskDialog.Show("Debug", $"Set height to 0 for {element.Name}");
            }
            else
            {
                //TaskDialog.Show("Debug", $"Failed to set height for {element.Name}");
            }
        }

        private Element FindElementByName(Document doc, string name)
        {
            var elements = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .WhereElementIsNotElementType()
                .ToElements();

            foreach (Element element in elements)
            {
                if (element.Name.Equals(name))
                {
                    return element;
                }
            }
            TaskDialog.Show("Debug", $"Element not found: {name}");
            return null;
        }

        private void MoveElementToLocation(Document doc, Element element, XYZ newLocation)
        {
            XYZ elementCenter = GetElementCenter(element);
            XYZ translation = newLocation - elementCenter;
            ElementTransformUtils.MoveElement(doc, element.Id, translation);
        }

        private XYZ GetElementCenter(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return (boundingBox.Min + boundingBox.Max) / 2.0;
        }

        private XYZ GetBoundingBoxMinPoint(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return boundingBox.Min;
        }

        private XYZ GetBoundingBoxMaxPoint(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return boundingBox.Max;
        }

        private double GetHeight(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return boundingBox.Max.Z - boundingBox.Min.Z;
        }

        private double GetZAtPosition(Element slab, double x, double y)
        {
            Autodesk.Revit.DB.Options geomOptions = new Autodesk.Revit.DB.Options();
            geomOptions.ComputeReferences = true;
            geomOptions.DetailLevel = ViewDetailLevel.Fine;
            GeometryElement geomElement = slab.get_Geometry(geomOptions);

            foreach (GeometryObject geomObj in geomElement)
            {
                if (geomObj is Solid solid)
                {
                    foreach (Face face in solid.Faces)
                    {
                        if (face is PlanarFace planarFace)
                        {
                            XYZ faceNormal = planarFace.FaceNormal;
                            if (Math.Abs(faceNormal.Z) > 0.01) // If not a vertical plane
                            {
                                XYZ pointOnFace = -planarFace.Origin;
                                double z = pointOnFace.Z - (faceNormal.X * (x - pointOnFace.X) + faceNormal.Y * (y - pointOnFace.Y)) / faceNormal.Z;
                                return z;
                            }
                        }
                    }
                }
            }

            return 0; // If no suitable face found
        }


    }

    [Transaction(TransactionMode.Manual)]
    public class CombineBridgeElements_Abutment_Abutment : IExternalCommand
    {
        private ExternalCommandData _commandData;
        private static Queue<Action> _actionQueue = new Queue<Action>();
        private static bool _isProcessing = false;
        private static DateTime _lastActionTime = DateTime.MinValue;
        private static readonly TimeSpan _delay = TimeSpan.FromMilliseconds(500); // 0.5 second delay

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Reset static state from previous runs
            _actionQueue.Clear();
            _isProcessing = false;

            _commandData = commandData;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            // Load ontology file and execute SPARQL query
            string ttlFilePath = @"C:\Users\chw42\source\repos\RevitBridge2\RevitBridge2\bin\Debug\WBS_Bridge.ttl"; // Update path as needed
            Graph g = new Graph();
            try
            {
                FileLoader.Load(g, ttlFilePath);
            }
            catch (Exception ex)
            {
                message = $"Ontology file loading failed: {ex.Message}";
                return Result.Failed;
            }

            // SPARQL query for A1 abutment
            string queryA1 = @"
PREFIX bso: <https://hyunwoochacha.github.io/SO-ASAS/ontology#>
PREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#>
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>

SELECT ?footingName ?foundationName ?wallName ?wingLeftName ?wingRightName ?capName WHERE {
    bso:A1_AbutmentFooting_Instance bso:name ?footingName .
    bso:A1_AbutmentFoundation_Instance bso:name ?foundationName .
    bso:A1_AbutmentWall_Instance bso:name ?wallName .
    bso:A1_AbutmentWingWall_Left_Instance bso:name ?wingLeftName .
    bso:A1_AbutmentWingWall_Right_Instance bso:name ?wingRightName .
    bso:A1_AbutmentCap_Instance bso:name ?capName .
}";

            // SPARQL query for A2 abutment
            string queryA2 = @"
PREFIX bso: <https://hyunwoochacha.github.io/SO-ASAS/ontology#>
PREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#>
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>

SELECT ?footingName ?foundationName ?wallName ?wingLeftName ?wingRightName ?capName WHERE {
    bso:A2_AbutmentFooting_Instance bso:name ?footingName .
    bso:A2_AbutmentFoundation_Instance bso:name ?foundationName .
    bso:A2_AbutmentWall_Instance bso:name ?wallName .
    bso:A2_AbutmentWingWall_Left_Instance bso:name ?wingLeftName .
    bso:A2_AbutmentWingWall_Right_Instance bso:name ?wingRightName .
    bso:A2_AbutmentCap_Instance bso:name ?capName .
}";

            // SPARQL query for superstructure
            string query = @"
PREFIX bso: <https://hyunwoochacha.github.io/SO-ASAS/ontology#>
PREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> 
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>

SELECT ?slabName ?protectivewallLeftName ?protectivewallRightName WHERE {
    bso:Slab_Instance bso:name ?slabName .
    bso:Protectivewall_Left_Instance bso:name ?protectivewallLeftName .
    bso:Protectivewall_Right_Instance bso:name ?protectivewallRightName .
}";

            // Execute SPARQL query and process results (superstructure)
            SparqlResultSet resultsA0 = (SparqlResultSet)g.ExecuteQuery(query);
            if (resultsA0 == null || resultsA0.Count == 0)
            {
                message = "SPARQL query failed (superstructure)";
                return Result.Failed;
            }

            // Execute SPARQL query and process results (A1)
            SparqlResultSet resultsA1 = (SparqlResultSet)g.ExecuteQuery(queryA1);
            if (resultsA1 == null || resultsA1.Count == 0)
            {
                message = "SPARQL query failed (A1)";
                return Result.Failed;
            }

            // Execute SPARQL query and process results (A2)
            SparqlResultSet resultsA2 = (SparqlResultSet)g.ExecuteQuery(queryA2);
            if (resultsA2 == null || resultsA2.Count == 0)
            {
                message = "SPARQL query failed (A2)";
                return Result.Failed;
            }

            // Execute SPARQL query and process results (superstructure)
            SparqlResultSet results = (SparqlResultSet)g.ExecuteQuery(query);
            if (results == null || results.Count == 0)
            {
                message = "SPARQL query failed (superstructure)";
                return Result.Failed;
            }

            // Add tasks to action queue
            EnqueueActions(doc, resultsA0, resultsA1, resultsA2, results);

            // Register Idling event and start processing
            if (!_isProcessing && _actionQueue.Count > 0)
            {
                UIApplication uiApp = _commandData.Application;
                uiApp.Idling += OnIdling;
                _isProcessing = true;
                _lastActionTime = DateTime.Now;
            }

            if (_actionQueue.Count == 0)           // No actions to process
            {
                message = "Pier-Pier: No targets to combine.";
                return Result.Failed;              // Do not return Succeeded
            }

            // Keep Idling registration as is
            if (!_isProcessing && _actionQueue.Count > 0)
            {
                UIApplication uiApp = _commandData.Application;
                uiApp.Idling += OnIdling;
                _isProcessing = true;
                _lastActionTime = DateTime.Now;
            }

            return Result.Succeeded;
        }

        private void EnqueueActions(Document doc, SparqlResultSet resultsA0, SparqlResultSet resultsA1, SparqlResultSet resultsA2, SparqlResultSet results)
        {
            string wallNameA1 = null; // A1 wall name

            // List to store superstructure ElementIds
            List<ElementId> superstructureElementIds = new List<ElementId>();

            // Hide superstructure elements first
            foreach (SparqlResult result in resultsA0)
            {
                string slabName = result["slabName"].ToString().Split('^')[0];
                string protectivewallLeftName = result["protectivewallLeftName"].ToString().Split('^')[0];
                string protectivewallRightName = result["protectivewallRightName"].ToString().Split('^')[0];

                // Create local variables
                string localSlabName = slabName;
                string localProtectivewallLeftName = protectivewallLeftName;
                string localProtectivewallRightName = protectivewallRightName;

                // Add hide action to queue
                _actionQueue.Enqueue(() =>
                {
                    // Find elements
                    Element slab = FindElementByName(doc, localSlabName);
                    Element protectiveWallLeft = FindElementByName(doc, localProtectivewallLeftName);
                    Element protectiveWallRight = FindElementByName(doc, localProtectivewallRightName);

                    // Collect superstructure element IDs
                    if (slab != null) superstructureElementIds.Add(slab.Id);
                    if (protectiveWallLeft != null) superstructureElementIds.Add(protectiveWallLeft.Id);
                    if (protectiveWallRight != null) superstructureElementIds.Add(protectiveWallRight.Id);

                    // Hide elements
                    if (superstructureElementIds.Count > 0)
                        doc.ActiveView.HideElements(superstructureElementIds);
                });
            }

            // Process A1 results and add actions
            foreach (SparqlResult result in resultsA1)
            {
                string footingName = result["footingName"].ToString().Split('^')[0];
                string foundationName = result["foundationName"].ToString().Split('^')[0];
                wallNameA1 = result["wallName"].ToString().Split('^')[0]; // Store value in wallNameA1
                string wingLeftName = result["wingLeftName"].ToString().Split('^')[0];
                string wingRightName = result["wingRightName"].ToString().Split('^')[0];
                string capName = result["capName"].ToString().Split('^')[0];

                // Create local variables to avoid closure issues
                string localFootingName = footingName;
                string localFoundationName = foundationName;
                string localWallName = wallNameA1;
                string localWingLeftName = wingLeftName;
                string localWingRightName = wingRightName;
                string localCapName = capName;

                _actionQueue.Enqueue(() => CombineElements(doc, localFootingName, localFoundationName, localWallName, localWingLeftName, localWingRightName, localCapName, "A1"));
            }

            // Process superstructure elements and add actions
            foreach (SparqlResult result in results)
            {
                string slabName = result["slabName"].ToString().Split('^')[0];
                string protectivewallLeftName = result["protectivewallLeftName"].ToString().Split('^')[0];
                string protectivewallRightName = result["protectivewallRightName"].ToString().Split('^')[0];

                // Create local variables to avoid closure issues
                string localSlabName = slabName;
                string localProtectivewallLeftName = protectivewallLeftName;
                string localProtectivewallRightName = protectivewallRightName;
                string localWallNameA1 = wallNameA1; // Copy wallNameA1 to local variable

                _actionQueue.Enqueue(() =>
                {
                    Element slab = FindElementByName(doc, localSlabName);
                    Element wallA1 = FindElementByName(doc, localWallNameA1); // Modified


                    if (slab != null && wallA1 != null)
                    {
                        CombineSlabWithWall(doc, slab, wallA1);
                    }

                    // Move protective walls to slab position
                    Element protectiveWallLeft = FindElementByName(doc, localProtectivewallLeftName);
                    Element protectiveWallRight = FindElementByName(doc, localProtectivewallRightName);
                    if (slab != null && protectiveWallLeft != null && protectiveWallRight != null)
                    {
                        MoveProtectiveWallsToSlab(doc, slab, protectiveWallLeft, protectiveWallRight);
                    }

                    // Collect superstructure element IDs
                    if (slab != null) superstructureElementIds.Add(slab.Id);
                    if (protectiveWallLeft != null) superstructureElementIds.Add(protectiveWallLeft.Id);
                    if (protectiveWallRight != null) superstructureElementIds.Add(protectiveWallRight.Id);

                });
            }




            // Process A2 results and add actions
            foreach (SparqlResult result in resultsA2)
            {
                string footingName = result["footingName"].ToString().Split('^')[0];
                string foundationName = result["foundationName"].ToString().Split('^')[0];
                string wallName = result["wallName"].ToString().Split('^')[0];
                string wingLeftName = result["wingLeftName"].ToString().Split('^')[0];
                string wingRightName = result["wingRightName"].ToString().Split('^')[0];
                string capName = result["capName"].ToString().Split('^')[0];

                // Create local variables to avoid closure issues
                string localFootingName = footingName;
                string localFoundationName = foundationName;
                string localWallName = wallName;
                string localWingLeftName = wingLeftName;
                string localWingRightName = wingRightName;
                string localCapName = capName;

                _actionQueue.Enqueue(() =>
                {
                    // Move A2 wall relative to slab
                    Element slab = FindElementByName(doc, "Slab");
                    Element wallA2 = FindElementByName(doc, localWallName);
                    if (slab != null && wallA2 != null)
                    {
                        MoveWallToSlab(doc, slab, wallA2);

                        // Set wall height
                        SetElementHeightToZero(doc, wallA2);

                        // Find each element of A2 abutment
                        Element wingLeftA2 = FindElementByName(doc, localWingLeftName);
                        Element wingRightA2 = FindElementByName(doc, localWingRightName);
                        Element foundationA2 = FindElementByName(doc, localFoundationName);
                        Element footingA2 = FindElementByName(doc, localFootingName);

                        // Wall and wing wall combination
                        if (wingLeftA2 != null)
                        {
                            CombineWingWithWall(doc, wallA2, wingLeftA2, isLeftWing: true);
                            SetElementHeightToZero(doc, wingLeftA2);
                        }

                        if (wingRightA2 != null)
                        {
                            CombineWingWithWall(doc, wallA2, wingRightA2, isLeftWing: false);
                        }

                        // Wall and foundation combination
                        if (foundationA2 != null)
                        {
                            CombineFoundationWithWall(doc, foundationA2, wallA2);
                            SetElementHeightToZero(doc, foundationA2);
                        }

                        // Foundation and footing combination
                        if (foundationA2 != null && footingA2 != null)
                        {
                            CombineFootingWithFoundation(doc, foundationA2, footingA2);
                        }
                    }
                });
            }

            // Unhide superstructure elements
            _actionQueue.Enqueue(() =>
            {
                doc.ActiveView.UnhideElements(superstructureElementIds);
            });

        }

        private void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            UIApplication uiApp = _commandData.Application;

            if (_actionQueue.Count > 0)
            {
                DateTime currentTime = DateTime.Now;
                if ((currentTime - _lastActionTime) >= _delay)
                {
                    Action action = _actionQueue.Dequeue();
                    try
                    {
                        UIDocument uidoc = uiApp.ActiveUIDocument;
                        Document doc = uidoc.Document;

                        using (Transaction trans = new Transaction(doc, "Combine Bridge Elements"))
                        {
                            trans.Start();
                            action.Invoke();
                            trans.Commit();
                        }

                        _lastActionTime = DateTime.Now;
                    }
                    catch (Exception ex)
                    {
                        TaskDialog.Show("Error", $"Error occurred during action execution: {ex.Message}");
                        _actionQueue.Clear(); // Clear queue, stop processing
                        uiApp.Idling -= OnIdling;
                        _isProcessing = false;
                    }
                }
            }
            else
            {
                uiApp.Idling -= OnIdling;
                _isProcessing = false;
                TaskDialog.Show("Info", "Abutment and upper structure combination completed.");
            }
        }



        private void CombineElements(Document doc, string footingName, string foundationName, string wallName, string wingLeftName, string wingRightName, string capName, string abutmentId)
        {
            var footing = FindElementByName(doc, footingName);
            var foundation = FindElementByName(doc, foundationName);
            var wall = FindElementByName(doc, wallName);
            var wingLeft = FindElementByName(doc, wingLeftName);
            var wingRight = FindElementByName(doc, wingRightName);
            var cap = FindElementByName(doc, capName);

            if (footing != null && foundation != null)
            {
                XYZ foundationLocation = new XYZ(GetElementCenter(footing).X, GetElementCenter(footing).Y, GetBoundingBoxMaxPoint(footing).Z + GetHeight(foundation) / 2);
                MoveElementToLocation(foundation, foundationLocation);
            }

            if (wall != null)
            {
                MoveWallAndWingsToFoundationTop(doc, foundation, wall, wingLeft, wingRight, abutmentId);
            }

            if (cap != null && wall != null)
            {
                XYZ capLocation = new XYZ(GetElementCenter(wall).X, GetElementCenter(wall).Y, GetBoundingBoxMaxPoint(wall).Z + GetHeight(cap) / 2);
                MoveElementToLocation(cap, capLocation);
            }
        }

        private void CombineSlabWithWall(Document doc, Element slab, Element wall)
        {
            // Get wall base length
            double wallLength = GetBoundingBoxMaxPoint(wall).X - GetBoundingBoxMinPoint(wall).X;

            // Get wall top center point
            XYZ wallTopCenter = GetTopCenterPoint(wall);

            // Get slab bottom center point
            XYZ slabBottomCenter = GetBottomCenterPoint(slab);

            // Calculate translation vector to position slab on wall
            XYZ translation = new XYZ(
                wallTopCenter.X - slabBottomCenter.X - wallLength,
                wallTopCenter.Y - slabBottomCenter.Y,
                wallTopCenter.Z - slabBottomCenter.Z - GetHeight(wall)
            );

            // Move slab
            ElementTransformUtils.MoveElement(doc, slab.Id, translation);
        }

        private void MoveProtectiveWallsToSlab(Document doc, Element slab, Element protectiveWallLeft, Element protectiveWallRight)
        {
            // Slab position info
            XYZ slabMinPoint = GetBoundingBoxMinPoint(slab);
            XYZ slabMaxPoint = GetBoundingBoxMaxPoint(slab);

            // Calculate slab X-axis center
            double slabCenterX = (slabMinPoint.X + slabMaxPoint.X) / 2.0;

            // Move left protective wall position
            if (protectiveWallLeft != null)
            {
                XYZ newLocationLeft = new XYZ(
                    slabCenterX,
                    slabMinPoint.Y + (slabMaxPoint.Y - slabMinPoint.Y) / 2.0,  // Position at slab Y-axis left edge
                    slabMaxPoint.Z    // Z position same as slab top
                );
                MoveElementToLocation(protectiveWallLeft, newLocationLeft);
                SetElementHeightToZero(doc, protectiveWallLeft);
            }

            // Move right protective wall position
            if (protectiveWallRight != null)
            {
                XYZ newLocationRight = new XYZ(
                    slabCenterX,
                    slabMinPoint.Y + (slabMaxPoint.Y - slabMinPoint.Y) / 2.0,  // Position at slab Y-axis right edge
                    slabMaxPoint.Z    // Z position same as slab top
                );
                MoveElementToLocation(protectiveWallRight, newLocationRight);
                SetElementHeightToZero(doc, protectiveWallRight);
            }
        }

        private void MoveWallToSlab(Document doc, Element slab, Element wall)
        {
            // Get slab base length
            double slabLength = GetBoundingBoxMaxPoint(slab).X - GetBoundingBoxMinPoint(slab).X;

            // Get wall base length
            double wallLength = GetBoundingBoxMaxPoint(wall).X - GetBoundingBoxMinPoint(wall).X;

            // Get slab bottom center point
            XYZ slabBottomCenter = GetBottomCenterPoint(slab);

            // Get wall top center point
            XYZ wallTopCenter = GetTopCenterPoint(wall);

            // Get wall bottom center point
            XYZ wallBottomCenter = GetBottomCenterPoint(wall);

            // Calculate translation vector to position wall below slab
            XYZ translation = new XYZ(
                slabBottomCenter.X - wallTopCenter.X + wallLength + slabLength - wallLength * 2 / 5,
                slabBottomCenter.Y - wallTopCenter.Y,
                slabBottomCenter.Z - wallTopCenter.Z + GetHeight(wall)
            );

            // Move wall
            ElementTransformUtils.MoveElement(doc, wall.Id, translation);
        }

        private void SetElementHeightToZero(Document doc, Element element)
        {
            // Set "Height Offset from Level" parameter to 0
            Parameter heightParam = element.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
            if (heightParam != null && !heightParam.IsReadOnly)
            {
                heightParam.Set(0.0);
            }
            else
            {
                TaskDialog.Show("Debug", $"Failed to set height for {element.Name}");
            }
        }

        private XYZ GetTopCenterPoint(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return new XYZ(
                boundingBox.Max.X,
                (boundingBox.Min.Y + boundingBox.Max.Y) / 2,
                boundingBox.Max.Z
            );
        }

        private XYZ GetBottomCenterPoint(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return new XYZ(
                boundingBox.Min.X,
                (boundingBox.Min.Y + boundingBox.Max.Y) / 2,
                boundingBox.Min.Z
            );
        }
        private XYZ GetBottomCenterPoint2(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return new XYZ(
                boundingBox.Max.X,
                (boundingBox.Min.Y + boundingBox.Max.Y) / 2,
                boundingBox.Min.Z
            );
        }

        private void MoveWallAndWingsToFoundationTop(Document doc, Element foundation, Element wall, Element wingLeft, Element wingRight, string abutmentId)
        {
            // 1. Move wall above foundation
            XYZ translation;
            if (abutmentId == "A1")
            {
                // A1: Move wall to left edge of foundation
                XYZ foundationTopLeft = new XYZ(GetBoundingBoxMaxPoint(foundation).X, GetBoundingBoxMinPoint(foundation).Y, GetBoundingBoxMaxPoint(foundation).Z);
                XYZ wallBottomLeft = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                translation = foundationTopLeft - wallBottomLeft;

                // Get wall base length
                double wallLength = GetBoundingBoxMaxPoint(wall).X - GetBoundingBoxMinPoint(wall).X;

                // Move wall right by wall length
                translation = foundationTopLeft - wallBottomLeft - new XYZ(wallLength * 2 / 5, 0, 0);
            }
            else if (abutmentId == "A2")
            {
                // A2: Keep centered as per original code
                XYZ foundationTopCenter = new XYZ(GetBoundingBoxMinPoint(foundation).X, GetBoundingBoxMinPoint(foundation).Y, GetBoundingBoxMaxPoint(foundation).Z);
                XYZ wallBottomCenter = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                translation = foundationTopCenter - wallBottomCenter;
            }
            else
            {
                // Default: center placement (for abutmentId other than A1, A2)
                XYZ foundationTopCenter = new XYZ(GetBoundingBoxMinPoint(foundation).X, GetBoundingBoxMinPoint(foundation).Y, GetBoundingBoxMaxPoint(foundation).Z);
                XYZ wallBottomCenter = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                translation = foundationTopCenter - wallBottomCenter;
            }
            MoveElementToLocation(wall, GetElementCenter(wall) + translation);

            // 2. Align left wing wall to wall
            if (wingLeft != null)
            {
                XYZ wingLeftTranslation = CalculateTranslationForLeftWing(wall, wingLeft, abutmentId);
                MoveElementToLocation(wingLeft, GetElementCenter(wingLeft) + wingLeftTranslation);
            }

            // 3. Align right wing wall to wall
            if (wingRight != null)
            {
                XYZ wingRightTranslation = CalculateTranslationForRightWing(wall, wingRight, abutmentId);
                MoveElementToLocation(wingRight, GetElementCenter(wingRight) + wingRightTranslation);
            }
        }

        private XYZ CalculateTranslationForLeftWing(Element wall, Element wingLeft, string abutmentId)
        {
            XYZ wallCorner, wingLeftCorner;

            if (abutmentId == "A1")
            {
                // A1 case: calculate left corner alignment
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMaxPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingLeftCorner = new XYZ(GetBoundingBoxMinPoint(wingLeft).X, GetBoundingBoxMaxPoint(wingLeft).Y, GetBoundingBoxMinPoint(wingLeft).Z);

                // Get wing wall base length
                double wingLength = GetBoundingBoxMaxPoint(wingLeft).X - GetBoundingBoxMinPoint(wingLeft).X;
                // Get wall base length
                double wallLength = GetBoundingBoxMaxPoint(wall).X - GetBoundingBoxMinPoint(wall).X;

                // Calculate vector to move to left edge of wall
                return wallCorner - wingLeftCorner - new XYZ(wingLength, 0, 0) + new XYZ(wallLength * 2 / 5, 0, 0);

            }
            else
            {
                // A2 case: align left wing wall to left edge of wall
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMaxPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingLeftCorner = new XYZ(GetBoundingBoxMinPoint(wingLeft).X, GetBoundingBoxMaxPoint(wingLeft).Y, GetBoundingBoxMinPoint(wingLeft).Z);

                // Calculate vector to move to left edge of wall
                return wallCorner - wingLeftCorner;
            }
        }

        private XYZ CalculateTranslationForRightWing(Element wall, Element wingRight, string abutmentId)
        {
            XYZ wallCorner, wingRightCorner;

            if (abutmentId == "A1")
            {
                // A1 case: calculate right corner alignment
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingRightCorner = new XYZ(GetBoundingBoxMinPoint(wingRight).X, GetBoundingBoxMinPoint(wingRight).Y, GetBoundingBoxMinPoint(wingRight).Z);

                // Get wall base length
                double wingLength = GetBoundingBoxMaxPoint(wingRight).X - GetBoundingBoxMinPoint(wingRight).X;

                // Get wall base length
                double wallLength = GetBoundingBoxMaxPoint(wall).X - GetBoundingBoxMinPoint(wall).X;

                // Calculate vector to move to right edge of wall
                return wallCorner - wingRightCorner - new XYZ(wingLength, 0, 0) + new XYZ(wallLength * 2 / 5, 0, 0);
            }
            else
            {
                // A2 case: align right wing wall to right edge of wall
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingRightCorner = new XYZ(GetBoundingBoxMinPoint(wingRight).X, GetBoundingBoxMinPoint(wingRight).Y, GetBoundingBoxMinPoint(wingRight).Z);
            }

            // Calculate vector to move to left edge of wall
            return wallCorner - wingRightCorner;
        }

        private void CombineWingWithWall(Document doc, Element wall, Element wing, bool isLeftWing)
        {
            XYZ wallCorner, wingCorner;

            if (isLeftWing)
            {
                // A2 case: align left wing wall to left edge of wall
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMaxPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingCorner = new XYZ(GetBoundingBoxMinPoint(wing).X, GetBoundingBoxMaxPoint(wing).Y, GetBoundingBoxMinPoint(wing).Z);
            }
            else
            {
                // A2 case: align right wing wall to right edge of wall
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingCorner = new XYZ(GetBoundingBoxMinPoint(wing).X, GetBoundingBoxMinPoint(wing).Y, GetBoundingBoxMinPoint(wing).Z);
            }

            // Calculate vector to move to wall edge
            XYZ translation = wallCorner - wingCorner;

            // Move wing wall
            MoveElementToLocation(wing, GetElementCenter(wing) + translation);
        }

        private void CombineFoundationWithWall(Document doc, Element foundation, Element wall)
        {
            // Calculate vector to align wall bottom with foundation top
            XYZ wallBottomRightCorner = GetBottomRightCorner(wall);
            XYZ foundationTopRightCorner = GetTopRightCorner(foundation);

            // Calculate base vector for wall and foundation alignment
            XYZ translation = wallBottomRightCorner - foundationTopRightCorner;

            // Additional X-axis translation
            double wallLength = GetBoundingBoxMinPoint(wall).X - GetBoundingBoxMaxPoint(wall).X;

            // Move foundation
            MoveElementToLocation(foundation, GetElementCenter(foundation) + translation);
        }

        private void CombineFootingWithFoundation(Document doc, Element foundation, Element footing)
        {
            // Get foundation bottom min coordinates (min point, X,Y,Z)
            XYZ foundationBottomLeft = GetBoundingBoxMinPoint(foundation);

            // Get footing top center point
            XYZ foundationBottomRight = GetBoundingBoxMaxPoint(foundation);

            // Calculate translation vector (X,Y: foundation center, Z: foundation bottom)
            XYZ translation = new XYZ(
                (foundationBottomLeft.X + foundationBottomRight.X) / 2,  // X-axis translation
                (foundationBottomLeft.Y + foundationBottomRight.Y) / 2,  // Y-axis translation
                foundationBottomLeft.Z   // Z-axis translation
            );

            // Move footing below foundation
            MoveElementToLocation(footing, translation);
        }



        private Element FindElementByName(Document doc, string name)
        {
            var elements = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType().ToElements();
            foreach (Element element in elements)
            {
                if (element.Name.Equals(name))
                {
                    return element;
                }
            }
            return null;
        }

        private XYZ GetBottomRightCorner(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return new XYZ(
                boundingBox.Min.X,
                boundingBox.Min.Y,
                boundingBox.Min.Z
            );
        }

        private XYZ GetTopRightCorner(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return new XYZ(
                boundingBox.Min.X,
                boundingBox.Min.Y,
                boundingBox.Max.Z
            );
        }

        private XYZ GetBoundingBoxMinPoint2(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return boundingBox.Min;
        }

        private XYZ GetBoundingBoxMaxPoint2(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return boundingBox.Max;
        }
        private XYZ GetBoundingBoxMinPoint(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return boundingBox.Min;
        }

        private XYZ GetBoundingBoxMaxPoint(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return boundingBox.Max;
        }

        private double GetHeight(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return boundingBox.Max.Z - boundingBox.Min.Z;
        }

        private void MoveElementToLocation(Element element, XYZ newLocation)
        {
            XYZ elementCenter = GetElementCenter(element);
            XYZ translation = newLocation - elementCenter;
            ElementTransformUtils.MoveElement(element.Document, element.Id, translation);
        }

        private XYZ GetElementCenter(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return (boundingBox.Min + boundingBox.Max) / 2.0;
        }
    }


    [Transaction(TransactionMode.Manual)]
    public class CombineBridgeElements_Abutment_Pier : IExternalCommand
    {
        private ExternalCommandData _commandData;
        private static Queue<Action> _actionQueue = new Queue<Action>();
        private static bool _isProcessing = false;
        private static DateTime _lastActionTime = DateTime.MinValue;
        private static readonly TimeSpan _delay = TimeSpan.FromMilliseconds(500); // 0.5 second delay
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Reset static state from previous runs
            _actionQueue.Clear();
            _isProcessing = false;

            _commandData = commandData;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            // Load ontology file and execute SPARQL query
            string ttlFilePath = @"C:\Users\chw42\source\repos\RevitBridge2\RevitBridge2\bin\Debug\WBS_Bridge.ttl"; // Update path as needed
            Graph g = new Graph();
            try
            {
                FileLoader.Load(g, ttlFilePath);
            }
            catch (Exception ex)
            {
                message = $"Ontology file loading failed: {ex.Message}";
                return Result.Failed;
            }

            // SPARQL query for A2 abutment
            string queryA2 = @"
PREFIX bso: <https://hyunwoochacha.github.io/SO-ASAS/ontology#>
PREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#>
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>

SELECT ?footingName ?foundationName ?wallName ?wingLeftName ?wingRightName ?capName WHERE {
    bso:A2_AbutmentFooting_Instance bso:name ?footingName .
    bso:A2_AbutmentFoundation_Instance bso:name ?foundationName .
    bso:A2_AbutmentWall_Instance bso:name ?wallName .
    bso:A2_AbutmentWingWall_Left_Instance bso:name ?wingLeftName .
    bso:A2_AbutmentWingWall_Right_Instance bso:name ?wingRightName .
    bso:A2_AbutmentCap_Instance bso:name ?capName .
}";

            // SPARQL query for superstructure
            string querySuperstructure = @"
PREFIX bso: <https://hyunwoochacha.github.io/SO-ASAS/ontology#>
PREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> 
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>

SELECT ?slabName ?protectivewallLeftName ?protectivewallRightName WHERE {
    bso:Slab_Instance bso:name ?slabName .
    bso:Protectivewall_Left_Instance bso:name ?protectivewallLeftName .
    bso:Protectivewall_Right_Instance bso:name ?protectivewallRightName .
}";

            // SPARQL query for pier elements
            string queryPier = @"
PREFIX bso: <https://hyunwoochacha.github.io/SO-ASAS/ontology#>
PREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#>
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>

SELECT ?footingName ?foundationName ?columnName ?copingName WHERE {
    bso:A1_PierFooting_Instance bso:name ?footingName .
    bso:A1_PierFoundation_Instance bso:name ?foundationName .
    bso:A1_PierColumn_Instance bso:name ?columnName .
    bso:A1_PierCoping_Instance bso:name ?copingName .
}";

            // Execute SPARQL query and process results (A2)
            SparqlResultSet resultsA2 = (SparqlResultSet)g.ExecuteQuery(queryA2);
            if (resultsA2 == null || resultsA2.Count == 0)
            {
                message = "SPARQL query failed (A2)";
                return Result.Failed;
            }

            // Execute SPARQL query and process results (superstructure)
            SparqlResultSet resultsSuperstructure = (SparqlResultSet)g.ExecuteQuery(querySuperstructure);
            if (resultsSuperstructure == null || resultsSuperstructure.Count == 0)
            {
                message = "SPARQL query failed (superstructure)";
                return Result.Failed;
            }

            // Execute SPARQL query and process results (Pier)
            SparqlResultSet resultsPier = (SparqlResultSet)g.ExecuteQuery(queryPier);
            if (resultsPier == null || resultsPier.Count == 0)
            {
                message = "SPARQL query failed (Pier)";
                return Result.Failed;
            }

            // Add tasks to action queue
            EnqueueActions(doc, resultsA2, resultsSuperstructure, resultsPier);

            // Register Idling event and start processing
            if (!_isProcessing && _actionQueue.Count > 0)
            {
                UIApplication uiApp = _commandData.Application;
                uiApp.Idling += OnIdling;
                _isProcessing = true;
                _lastActionTime = DateTime.Now;
            }

            return Result.Succeeded;
        }

        private void EnqueueActions(Document doc, SparqlResultSet resultsA2, SparqlResultSet resultsSuperstructure, SparqlResultSet resultsPier)
        {
            string wallNameA2 = null; // A1 wall name
            string columnNamePier = null; // Pier columnName

            // List to store superstructure ElementIds
            List<ElementId> superstructureElementIds = new List<ElementId>();

            // Hide superstructure elements first
            foreach (SparqlResult result in resultsSuperstructure)
            {
                string slabName = result["slabName"].ToString().Split('^')[0];
                string protectivewallLeftName = result["protectivewallLeftName"].ToString().Split('^')[0];
                string protectivewallRightName = result["protectivewallRightName"].ToString().Split('^')[0];

                // Create local variables
                string localSlabName = slabName;
                string localProtectivewallLeftName = protectivewallLeftName;
                string localProtectivewallRightName = protectivewallRightName;

                // Add hide action to queue
                _actionQueue.Enqueue(() =>
                {
                    // Find elements
                    Element slab = FindElementByName(doc, localSlabName);
                    Element protectiveWallLeft = FindElementByName(doc, localProtectivewallLeftName);
                    Element protectiveWallRight = FindElementByName(doc, localProtectivewallRightName);

                    // Collect superstructure element IDs
                    if (slab != null) superstructureElementIds.Add(slab.Id);
                    if (protectiveWallLeft != null) superstructureElementIds.Add(protectiveWallLeft.Id);
                    if (protectiveWallRight != null) superstructureElementIds.Add(protectiveWallRight.Id);

                    // Hide elements
                    if (superstructureElementIds.Count > 0)
                        doc.ActiveView.HideElements(superstructureElementIds);
                });
            }

            // Process Pier results and add actions
            foreach (SparqlResult result in resultsPier)
            {
                string footingName = result["footingName"].ToString().Split('^')[0];
                string foundationName = result["foundationName"].ToString().Split('^')[0];
                string columnName = result["columnName"].ToString().Split('^')[0];
                string copingName = result["copingName"].ToString().Split('^')[0];

                // Store pier columnName
                columnNamePier = columnName;

                // Create local variables to avoid closure issues
                string localFootingName = footingName;
                string localFoundationName = foundationName;
                string localColumnName = columnName;
                string localCopingName = copingName;

                _actionQueue.Enqueue(() => CombinePierElements(doc, localFootingName, localFoundationName, localColumnName, localCopingName));
            }

            // Process superstructure elements and add actions
            foreach (SparqlResult result in resultsSuperstructure)
            {
                string slabName = result["slabName"].ToString().Split('^')[0];
                string protectivewallLeftName = result["protectivewallLeftName"].ToString().Split('^')[0];
                string protectivewallRightName = result["protectivewallRightName"].ToString().Split('^')[0];

                // Create local variables to avoid closure issues
                string localSlabName = slabName;
                string localProtectivewallLeftName = protectivewallLeftName;
                string localProtectivewallRightName = protectivewallRightName;
                string localColumnNamePier = columnNamePier; // Use pier columnName

                _actionQueue.Enqueue(() =>
                {
                    Element slab = FindElementByName(doc, localSlabName);
                    Element columnName = FindElementByName(doc, localColumnNamePier);

                    if (slab != null && columnName != null)
                    {
                        CombineSlabWithColumn(doc, slab, columnName);
                    }

                    // Move protective walls to slab position
                    Element protectiveWallLeft = FindElementByName(doc, localProtectivewallLeftName);
                    Element protectiveWallRight = FindElementByName(doc, localProtectivewallRightName);
                    if (slab != null && protectiveWallLeft != null && protectiveWallRight != null)
                    {
                        MoveProtectiveWallsToSlab(doc, slab, protectiveWallLeft, protectiveWallRight);
                    }

                    // Collect superstructure element IDs
                    if (slab != null) superstructureElementIds.Add(slab.Id);
                    if (protectiveWallLeft != null) superstructureElementIds.Add(protectiveWallLeft.Id);
                    if (protectiveWallRight != null) superstructureElementIds.Add(protectiveWallRight.Id);
                });
            }

            // Process A2 results and add actions
            foreach (SparqlResult result in resultsA2)
            {
                string footingName = result["footingName"].ToString().Split('^')[0];
                string foundationName = result["foundationName"].ToString().Split('^')[0];
                string wallName = result["wallName"].ToString().Split('^')[0];
                string wingLeftName = result["wingLeftName"].ToString().Split('^')[0];
                string wingRightName = result["wingRightName"].ToString().Split('^')[0];
                string capName = result["capName"].ToString().Split('^')[0];

                // Create local variables to avoid closure issues
                string localFootingName = footingName;
                string localFoundationName = foundationName;
                string localWallName = wallName;
                string localWingLeftName = wingLeftName;
                string localWingRightName = wingRightName;
                string localCapName = capName;

                _actionQueue.Enqueue(() =>
                {
                    // Move A2 wall relative to slab
                    Element slab = FindElementByName(doc, "Slab");
                    Element wallA2 = FindElementByName(doc, localWallName);
                    if (slab != null && wallA2 != null)
                    {
                        MoveWallToSlab(doc, slab, wallA2);

                        // Set wall height
                        SetElementHeightToZero(doc, wallA2);

                        // Find each element of A2 abutment
                        Element wingLeftA2 = FindElementByName(doc, localWingLeftName);
                        Element wingRightA2 = FindElementByName(doc, localWingRightName);
                        Element foundationA2 = FindElementByName(doc, localFoundationName);
                        Element footingA2 = FindElementByName(doc, localFootingName);

                        // Wall and wing wall combination
                        if (wingLeftA2 != null)
                        {
                            CombineWingWithWall(doc, wallA2, wingLeftA2, isLeftWing: true);
                            SetElementHeightToZero(doc, wingLeftA2);
                        }

                        if (wingRightA2 != null)
                        {
                            CombineWingWithWall(doc, wallA2, wingRightA2, isLeftWing: false);
                        }

                        // Wall and foundation combination
                        if (foundationA2 != null)
                        {
                            CombineFoundationWithWall(doc, foundationA2, wallA2);
                            SetElementHeightToZero(doc, foundationA2);
                        }

                        // Foundation and footing combination
                        if (foundationA2 != null && footingA2 != null)
                        {
                            CombineFootingWithFoundation(doc, foundationA2, footingA2);
                        }
                    }
                });
            }

            // Unhide superstructure elements
            _actionQueue.Enqueue(() =>
            {
                doc.ActiveView.UnhideElements(superstructureElementIds);
            });


        }

        // Abutment element combination method
        private void CombineAbutmentElements(Document doc, string footingName, string foundationName, string wallName, string wingLeftName, string wingRightName, string capName, string abutmentId)
        {
            var footing = FindElementByName(doc, footingName);
            var foundation = FindElementByName(doc, foundationName);
            var wall = FindElementByName(doc, wallName);
            var wingLeft = FindElementByName(doc, wingLeftName);
            var wingRight = FindElementByName(doc, wingRightName);
            var cap = FindElementByName(doc, capName);

            if (footing != null && foundation != null)
            {
                XYZ foundationLocation = new XYZ(GetElementCenter(footing).X, GetElementCenter(footing).Y, GetBoundingBoxMaxPoint(footing).Z + GetHeight(foundation) / 2);
                MoveElementToLocation(foundation, foundationLocation);
            }

            if (wall != null)
            {
                MoveWallAndWingsToFoundationTop(doc, foundation, wall, wingLeft, wingRight, abutmentId);
            }

            if (cap != null && wall != null)
            {
                XYZ capLocation = new XYZ(GetElementCenter(wall).X, GetElementCenter(wall).Y, GetBoundingBoxMaxPoint(wall).Z + GetHeight(cap) / 2);
                MoveElementToLocation(cap, capLocation);
            }
        }

        private void CombinePierElements(Document doc, string footingName, string foundationName, string columnName, string copingName)
        {
            var footing = FindElementByName(doc, footingName);
            var foundation = FindElementByName(doc, foundationName);
            var column = FindElementByName(doc, columnName);
            var coping = FindElementByName(doc, copingName);

            if (footing != null && foundation != null)
            {
                XYZ foundationLocation = new XYZ(GetElementCenter(footing).X, GetElementCenter(footing).Y, GetBoundingBoxMaxPoint(footing).Z + GetHeight(foundation) / 2);
                MoveElementToLocation(foundation, foundationLocation);
            }

            if (column != null)
            {
                MoveColumnToFoundationTop(doc, foundation, column);
            }

            if (coping != null && column != null)
            {
                XYZ copingLocation = new XYZ(GetElementCenter(column).X, GetElementCenter(column).Y, GetBoundingBoxMaxPoint(column).Z + GetHeight(coping) / 2);
                MoveElementToLocation(coping, copingLocation);
            }
        }

        private void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            UIApplication uiApp = _commandData.Application;

            if (_actionQueue.Count > 0)
            {
                DateTime currentTime = DateTime.Now;
                if ((currentTime - _lastActionTime) >= _delay)
                {
                    Action action = _actionQueue.Dequeue();
                    try
                    {
                        UIDocument uidoc = uiApp.ActiveUIDocument;
                        Document doc = uidoc.Document;

                        using (Transaction trans = new Transaction(doc, "Combine Bridge Elements"))
                        {
                            trans.Start();
                            action.Invoke();
                            trans.Commit();
                        }

                        _lastActionTime = DateTime.Now;
                    }
                    catch (Exception ex)
                    {
                        TaskDialog.Show("Error", $"Error occurred during action execution: {ex.Message}");
                        _actionQueue.Clear(); // Clear queue, stop processing
                        uiApp.Idling -= OnIdling;
                        _isProcessing = false;
                    }
                }
            }
            else
            {
                uiApp.Idling -= OnIdling;
                _isProcessing = false;
                TaskDialog.Show("Info", "Abutment, upper structure and Pier combination completed.");
            }
        }

        private void CombineElements(Document doc, string footingName, string foundationName, string columnName, string copingName)
        {
            var footing = FindElementByName(doc, footingName);
            var foundation = FindElementByName(doc, foundationName);
            var column = FindElementByName(doc, columnName);
            var coping = FindElementByName(doc, copingName);

            if (footing != null && foundation != null)
            {
                XYZ foundationLocation = new XYZ(GetElementCenter(footing).X, GetElementCenter(footing).Y, GetBoundingBoxMaxPoint(footing).Z + GetHeight(foundation) / 2);
                MoveElementToLocation(foundation, foundationLocation);
            }

            if (column != null)
            {
                MoveColumnToFoundationTop(doc, foundation, column);
            }

            if (coping != null && column != null)
            {
                XYZ copingLocation = new XYZ(GetElementCenter(column).X, GetElementCenter(column).Y, GetBoundingBoxMaxPoint(column).Z + GetHeight(coping) / 2);
                MoveElementToLocation(coping, copingLocation);
            }
        }

        private void CombineSlabWithColumn(Document doc, Element slab, Element column)
        {
            // Get column base length
            double columnLength = GetBoundingBoxMaxPoint(column).X - GetBoundingBoxMinPoint(column).X;

            // Get column top center point
            XYZ columnTopCenter = GetTopCenterPoint(column);

            // Get slab bottom center point
            XYZ slabBottomCenter = GetBottomCenterPoint(slab);

            // Calculate translation vector to position slab above column
            XYZ translation = new XYZ(
                columnTopCenter.X - slabBottomCenter.X - columnLength,
                columnTopCenter.Y - slabBottomCenter.Y,
                columnTopCenter.Z - slabBottomCenter.Z - GetHeight(column)
            );

            // Move slab
            ElementTransformUtils.MoveElement(doc, slab.Id, translation);
        }

        private void MoveProtectiveWallsToSlab(Document doc, Element slab, Element protectiveWallLeft, Element protectiveWallRight)
        {
            // Slab position info
            XYZ slabMinPoint = GetBoundingBoxMinPoint(slab);
            XYZ slabMaxPoint = GetBoundingBoxMaxPoint(slab);

            // Calculate slab X-axis center
            double slabCenterX = (slabMinPoint.X + slabMaxPoint.X) / 2.0;

            // Move left protective wall position
            if (protectiveWallLeft != null)
            {
                XYZ newLocationLeft = new XYZ(
                    slabCenterX,
                    slabMinPoint.Y + (slabMaxPoint.Y - slabMinPoint.Y) / 2.0,  // Position at slab Y-axis left edge
                    slabMaxPoint.Z    // Z position same as slab top
                );
                MoveElementToLocation(protectiveWallLeft, newLocationLeft);
                SetElementHeightToZero(doc, protectiveWallLeft);
            }

            // Move right protective wall position
            if (protectiveWallRight != null)
            {
                XYZ newLocationRight = new XYZ(
                    slabCenterX,
                    slabMinPoint.Y + (slabMaxPoint.Y - slabMinPoint.Y) / 2.0,  // Position at slab Y-axis right edge
                    slabMaxPoint.Z    // Z position same as slab top
                );
                MoveElementToLocation(protectiveWallRight, newLocationRight);
                SetElementHeightToZero(doc, protectiveWallRight);
            }
        }

        private void MoveWallToSlab(Document doc, Element slab, Element wall)
        {
            // Get slab base length
            double slabLength = GetBoundingBoxMaxPoint(slab).X - GetBoundingBoxMinPoint(slab).X;

            // Get wall base length
            double wallLength = GetBoundingBoxMaxPoint(wall).X - GetBoundingBoxMinPoint(wall).X;

            // Get slab bottom center point
            XYZ slabBottomCenter = GetBottomCenterPoint(slab);

            // Get wall top center point
            XYZ wallTopCenter = GetTopCenterPoint(wall);

            // Get wall bottom center point
            XYZ wallBottomCenter = GetBottomCenterPoint(wall);

            // Calculate translation vector to position wall below slab
            XYZ translation = new XYZ(
                slabBottomCenter.X - wallTopCenter.X + wallLength + slabLength - wallLength * 2 / 5,
                slabBottomCenter.Y - wallTopCenter.Y,
                slabBottomCenter.Z - wallTopCenter.Z + GetHeight(wall)
            );

            // Move wall
            ElementTransformUtils.MoveElement(doc, wall.Id, translation);
        }

        private void SetElementHeightToZero(Document doc, Element element)
        {
            // Set "Height Offset from Level" parameter to 0
            Parameter heightParam = element.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
            if (heightParam != null && !heightParam.IsReadOnly)
            {
                heightParam.Set(0.0);
            }
            else
            {
                TaskDialog.Show("Debug", $"Failed to set height for {element.Name}");
            }
        }

        private XYZ GetTopCenterPoint(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return new XYZ(
                boundingBox.Max.X,
                (boundingBox.Min.Y + boundingBox.Max.Y) / 2,
                boundingBox.Max.Z
            );
        }

        private XYZ GetBottomCenterPoint(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return new XYZ(
                boundingBox.Min.X,
                (boundingBox.Min.Y + boundingBox.Max.Y) / 2,
                boundingBox.Min.Z
            );
        }
        private XYZ GetBottomCenterPoint2(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return new XYZ(
                boundingBox.Max.X,
                (boundingBox.Min.Y + boundingBox.Max.Y) / 2,
                boundingBox.Min.Z
            );
        }

        private void MoveWallAndWingsToFoundationTop(Document doc, Element foundation, Element wall, Element wingLeft, Element wingRight, string abutmentId)
        {
            // 1. Move wall above foundation
            XYZ translation;
            if (abutmentId == "A1")
            {
                // A1: Move wall to left edge of foundation
                XYZ foundationTopLeft = new XYZ(GetBoundingBoxMaxPoint(foundation).X, GetBoundingBoxMinPoint(foundation).Y, GetBoundingBoxMaxPoint(foundation).Z);
                XYZ wallBottomLeft = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                translation = foundationTopLeft - wallBottomLeft;

                // Get wall base length
                double wallLength = GetBoundingBoxMaxPoint(wall).X - GetBoundingBoxMinPoint(wall).X;

                // Move wall right by wall length
                translation = foundationTopLeft - wallBottomLeft - new XYZ(wallLength * 2 / 5, 0, 0);
            }
            else if (abutmentId == "A2")
            {
                // A2: Keep centered as per original code
                XYZ foundationTopCenter = new XYZ(GetBoundingBoxMinPoint(foundation).X, GetBoundingBoxMinPoint(foundation).Y, GetBoundingBoxMaxPoint(foundation).Z);
                XYZ wallBottomCenter = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                translation = foundationTopCenter - wallBottomCenter;
            }
            else
            {
                // Default: center placement (for abutmentId other than A1, A2)
                XYZ foundationTopCenter = new XYZ(GetBoundingBoxMinPoint(foundation).X, GetBoundingBoxMinPoint(foundation).Y, GetBoundingBoxMaxPoint(foundation).Z);
                XYZ wallBottomCenter = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                translation = foundationTopCenter - wallBottomCenter;
            }
            MoveElementToLocation(wall, GetElementCenter(wall) + translation);

            // 2. Align left wing wall to wall
            if (wingLeft != null)
            {
                XYZ wingLeftTranslation = CalculateTranslationForLeftWing(wall, wingLeft, abutmentId);
                MoveElementToLocation(wingLeft, GetElementCenter(wingLeft) + wingLeftTranslation);
            }

            // 3. Align right wing wall to wall
            if (wingRight != null)
            {
                XYZ wingRightTranslation = CalculateTranslationForRightWing(wall, wingRight, abutmentId);
                MoveElementToLocation(wingRight, GetElementCenter(wingRight) + wingRightTranslation);
            }
        }

        private XYZ CalculateTranslationForLeftWing(Element wall, Element wingLeft, string abutmentId)
        {
            XYZ wallCorner, wingLeftCorner;

            if (abutmentId == "A1")
            {
                // A1 case: calculate left corner alignment
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMaxPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingLeftCorner = new XYZ(GetBoundingBoxMinPoint(wingLeft).X, GetBoundingBoxMaxPoint(wingLeft).Y, GetBoundingBoxMinPoint(wingLeft).Z);

                // Get wing wall base length
                double wingLength = GetBoundingBoxMaxPoint(wingLeft).X - GetBoundingBoxMinPoint(wingLeft).X;
                // Get wall base length
                double wallLength = GetBoundingBoxMaxPoint(wall).X - GetBoundingBoxMinPoint(wall).X;

                // Calculate vector to move to left edge of wall
                return wallCorner - wingLeftCorner - new XYZ(wingLength, 0, 0) + new XYZ(wallLength * 2 / 5, 0, 0);


            }
            else
            {
                // A2: Calculate to align left wing wall to left edge of wall
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMaxPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingLeftCorner = new XYZ(GetBoundingBoxMinPoint(wingLeft).X, GetBoundingBoxMaxPoint(wingLeft).Y, GetBoundingBoxMinPoint(wingLeft).Z);

                // Calculate vector to move to left edge of wall
                return wallCorner - wingLeftCorner;
            }
        }

        private XYZ CalculateTranslationForRightWing(Element wall, Element wingRight, string abutmentId)
        {
            XYZ wallCorner, wingRightCorner;

            if (abutmentId == "A1")
            {
                // A1 case: calculate right corner alignment
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingRightCorner = new XYZ(GetBoundingBoxMinPoint(wingRight).X, GetBoundingBoxMinPoint(wingRight).Y, GetBoundingBoxMinPoint(wingRight).Z);

                // Get wall base length
                double wingLength = GetBoundingBoxMaxPoint(wingRight).X - GetBoundingBoxMinPoint(wingRight).X;

                // Get wall base length
                double wallLength = GetBoundingBoxMaxPoint(wall).X - GetBoundingBoxMinPoint(wall).X;

                // Calculate vector to move to right edge of wall
                return wallCorner - wingRightCorner - new XYZ(wingLength, 0, 0) + new XYZ(wallLength * 2 / 5, 0, 0);
            }
            else
            {
                // A2 case: align right wing wall to right edge of wall
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingRightCorner = new XYZ(GetBoundingBoxMinPoint(wingRight).X, GetBoundingBoxMinPoint(wingRight).Y, GetBoundingBoxMinPoint(wingRight).Z);
            }

            // Calculate vector to move to left edge of wall
            return wallCorner - wingRightCorner;
        }

        private void CombineWingWithWall(Document doc, Element wall, Element wing, bool isLeftWing)
        {
            XYZ wallCorner, wingCorner;

            if (isLeftWing)
            {
                // A2 case: align left wing wall to left edge of wall
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMaxPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingCorner = new XYZ(GetBoundingBoxMinPoint(wing).X, GetBoundingBoxMaxPoint(wing).Y, GetBoundingBoxMinPoint(wing).Z);
            }
            else
            {
                // A2 case: align right wing wall to right edge of wall
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingCorner = new XYZ(GetBoundingBoxMinPoint(wing).X, GetBoundingBoxMinPoint(wing).Y, GetBoundingBoxMinPoint(wing).Z);
            }

            // Calculate vector to move to wall edge
            XYZ translation = wallCorner - wingCorner;

            // Move wing wall
            MoveElementToLocation(wing, GetElementCenter(wing) + translation);
        }

        private void CombineFoundationWithWall(Document doc, Element foundation, Element wall)
        {
            // Calculate vector to align wall bottom with foundation top
            XYZ wallBottomRightCorner = GetBottomRightCorner(wall);
            XYZ foundationTopRightCorner = GetTopRightCorner(foundation);

            // Calculate base vector for wall and foundation alignment
            XYZ translation = wallBottomRightCorner - foundationTopRightCorner;

            // Additional X-axis translation
            double wallLength = GetBoundingBoxMinPoint(wall).X - GetBoundingBoxMaxPoint(wall).X;

            // Move foundation
            MoveElementToLocation(foundation, GetElementCenter(foundation) + translation);
        }

        private void CombineFootingWithFoundation(Document doc, Element foundation, Element footing)
        {
            // Get foundation bottom min coordinates (min point, X,Y,Z)
            XYZ foundationBottomLeft = GetBoundingBoxMinPoint(foundation);

            // Get footing top center point
            XYZ foundationBottomRight = GetBoundingBoxMaxPoint(foundation);

            // Calculate translation vector (X,Y: foundation center, Z: foundation bottom)
            XYZ translation = new XYZ(
                (foundationBottomLeft.X + foundationBottomRight.X) / 2,  // X-axis translation
                (foundationBottomLeft.Y + foundationBottomRight.Y) / 2,  // Y-axis translation
                foundationBottomLeft.Z   // Z-axis translation
            );

            // Move footing below foundation
            MoveElementToLocation(footing, translation);
        }

        private void MoveColumnToFoundationTop(Document doc, Element foundation, Element column)
        {
            // Calculate foundation X-axis center
            double foundationCenterX = (GetBoundingBoxMinPoint(foundation).X + GetBoundingBoxMaxPoint(foundation).X) / 2;

            // Keep existing code for foundation Y and Z axes
            double foundationCenterY = GetBoundingBoxMinPoint(foundation).Y;
            double foundationTopZ = GetBoundingBoxMaxPoint(foundation).Z;

            // Calculate column bottom center point
            XYZ columnBottomCenter = new XYZ(
                GetBoundingBoxMinPoint(column).X,
                GetBoundingBoxMinPoint(column).Y,
                GetBoundingBoxMinPoint(column).Z
            );

            // Calculate vector to move column to foundation center
            XYZ translation = new XYZ(
                foundationCenterX - columnBottomCenter.X,  // X-axis translation: column to foundation center
                foundationCenterY - columnBottomCenter.Y,  // Y-axis translation: keep existing
                foundationTopZ - columnBottomCenter.Z      // Z-axis translation: keep existing
            );

            // Move column
            ElementTransformUtils.MoveElement(doc, column.Id, translation);
        }



        private Element FindElementByName(Document doc, string name)
        {
            var elements = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType().ToElements();
            foreach (Element element in elements)
            {
                if (element.Name.Equals(name))
                {
                    return element;
                }
            }
            return null;
        }

        private XYZ GetBottomRightCorner(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return new XYZ(
                boundingBox.Min.X,
                boundingBox.Min.Y,
                boundingBox.Min.Z
            );
        }

        private XYZ GetTopRightCorner(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return new XYZ(
                boundingBox.Min.X,
                boundingBox.Min.Y,
                boundingBox.Max.Z
            );
        }

        private XYZ GetBoundingBoxMinPoint2(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return boundingBox.Min;
        }

        private XYZ GetBoundingBoxMaxPoint2(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return boundingBox.Max;
        }
        private XYZ GetBoundingBoxMinPoint(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return boundingBox.Min;
        }

        private XYZ GetBoundingBoxMaxPoint(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return boundingBox.Max;
        }

        private double GetHeight(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return boundingBox.Max.Z - boundingBox.Min.Z;
        }

        private void MoveElementToLocation(Element element, XYZ newLocation)
        {
            XYZ elementCenter = GetElementCenter(element);
            XYZ translation = newLocation - elementCenter;
            ElementTransformUtils.MoveElement(element.Document, element.Id, translation);
        }

        private XYZ GetElementCenter(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return (boundingBox.Min + boundingBox.Max) / 2.0;
        }
    }


    [Transaction(TransactionMode.Manual)]
    public class CombineBridgeElements_Pier_Abutment : IExternalCommand
    {
        private ExternalCommandData _commandData;
        private static Queue<Action> _actionQueue = new Queue<Action>();
        private static bool _isProcessing = false;
        private static DateTime _lastActionTime = DateTime.MinValue;
        private static readonly TimeSpan _delay = TimeSpan.FromMilliseconds(500); // 0.5 second delay

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Reset static state from previous runs
            _actionQueue.Clear();
            _isProcessing = false;

            _commandData = commandData;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            // Load ontology file and execute SPARQL query
            string ttlFilePath = @"C:\Users\chw42\source\repos\RevitBridge2\RevitBridge2\bin\Debug\WBS_Bridge.ttl";
            Graph g = new Graph();
            try
            {
                FileLoader.Load(g, ttlFilePath);
            }
            catch (Exception ex)
            {
                message = $"Ontology file loading failed: {ex.Message}";
                return Result.Failed;
            }

            // SPARQL query for A1 abutment
            string queryA1 = @"
PREFIX bso: <https://hyunwoochacha.github.io/SO-ASAS/ontology#>
PREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#>
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>

SELECT ?footingName ?foundationName ?wallName ?wingLeftName ?wingRightName ?capName WHERE {
    bso:A1_AbutmentFooting_Instance bso:name ?footingName .
    bso:A1_AbutmentFoundation_Instance bso:name ?foundationName .
    bso:A1_AbutmentWall_Instance bso:name ?wallName .
    bso:A1_AbutmentWingWall_Left_Instance bso:name ?wingLeftName .
    bso:A1_AbutmentWingWall_Right_Instance bso:name ?wingRightName .
    bso:A1_AbutmentCap_Instance bso:name ?capName .
}";

            // SPARQL query for superstructure
            string querySuperstructure = @"
PREFIX bso: <https://hyunwoochacha.github.io/SO-ASAS/ontology#>
PREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> 
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>

SELECT ?slabName ?protectivewallLeftName ?protectivewallRightName WHERE {
    bso:Slab_Instance bso:name ?slabName .
    bso:Protectivewall_Left_Instance bso:name ?protectivewallLeftName .
    bso:Protectivewall_Right_Instance bso:name ?protectivewallRightName .
}";

            // SPARQL query for pier elements
            string queryPier = @"
PREFIX bso: <https://hyunwoochacha.github.io/SO-ASAS/ontology#>
PREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#>
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>

SELECT ?footingName ?foundationName ?columnName ?copingName WHERE {
    bso:A1_PierFooting_Instance bso:name ?footingName .
    bso:A1_PierFoundation_Instance bso:name ?foundationName .
    bso:A1_PierColumn_Instance bso:name ?columnName .
    bso:A1_PierCoping_Instance bso:name ?copingName .
}";

            // Execute SPARQL query and process results (A1)
            SparqlResultSet resultsA1 = (SparqlResultSet)g.ExecuteQuery(queryA1);
            if (resultsA1 == null || resultsA1.Count == 0)
            {
                message = "SPARQL query failed (A1)";
                return Result.Failed;
            }

            // Execute SPARQL query and process results (superstructure)
            SparqlResultSet resultsSuperstructure = (SparqlResultSet)g.ExecuteQuery(querySuperstructure);
            if (resultsSuperstructure == null || resultsSuperstructure.Count == 0)
            {
                message = "SPARQL query failed (superstructure)";
                return Result.Failed;
            }

            // Execute SPARQL query and process results (Pier)
            SparqlResultSet resultsPier = (SparqlResultSet)g.ExecuteQuery(queryPier);
            if (resultsPier == null || resultsPier.Count == 0)
            {
                message = "SPARQL query failed (Pier)";
                return Result.Failed;
            }

            // Add tasks to action queue
            EnqueueActions(doc, resultsA1, resultsSuperstructure, resultsPier);

            // Register Idling event and start processing
            if (!_isProcessing && _actionQueue.Count > 0)
            {
                UIApplication uiApp = _commandData.Application;
                uiApp.Idling += OnIdling;
                _isProcessing = true;
                _lastActionTime = DateTime.Now;
            }

            return Result.Succeeded;
        }

        private void EnqueueActions(Document doc, SparqlResultSet resultsA1, SparqlResultSet resultsSuperstructure, SparqlResultSet resultsPier)
        {
            string wallNameA1 = null; // A1 wall name

            // List to store superstructure ElementIds
            List<ElementId> superstructureElementIds = new List<ElementId>();

            // 1. Hide superstructure elements
            _actionQueue.Enqueue(() =>
            {
                foreach (SparqlResult result in resultsSuperstructure)
                {
                    string slabName = result["slabName"].ToString().Split('^')[0];
                    string protectivewallLeftName = result["protectivewallLeftName"].ToString().Split('^')[0];
                    string protectivewallRightName = result["protectivewallRightName"].ToString().Split('^')[0];

                    // Create local variables
                    string localSlabName = slabName;
                    string localProtectivewallLeftName = protectivewallLeftName;
                    string localProtectivewallRightName = protectivewallRightName;

                    // Find elements
                    Element slab = FindElementByName(doc, localSlabName);
                    Element protectiveWallLeft = FindElementByName(doc, localProtectivewallLeftName);
                    Element protectiveWallRight = FindElementByName(doc, localProtectivewallRightName);

                    // Collect superstructure element IDs
                    if (slab != null) superstructureElementIds.Add(slab.Id);
                    if (protectiveWallLeft != null) superstructureElementIds.Add(protectiveWallLeft.Id);
                    if (protectiveWallRight != null) superstructureElementIds.Add(protectiveWallRight.Id);
                }

                // Hide elements
                if (superstructureElementIds.Count > 0)
                    doc.ActiveView.HideElements(superstructureElementIds);
            });

            // Process A1 results and add actions
            foreach (SparqlResult result in resultsA1)
            {
                string footingName = result["footingName"].ToString().Split('^')[0];
                string foundationName = result["foundationName"].ToString().Split('^')[0];
                wallNameA1 = result["wallName"].ToString().Split('^')[0]; // Store value in wallNameA1
                string wingLeftName = result["wingLeftName"].ToString().Split('^')[0];
                string wingRightName = result["wingRightName"].ToString().Split('^')[0];
                string capName = result["capName"].ToString().Split('^')[0];

                // Create local variables to avoid closure issues
                string localFootingName = footingName;
                string localFoundationName = foundationName;
                string localWallName = wallNameA1;
                string localWingLeftName = wingLeftName;
                string localWingRightName = wingRightName;
                string localCapName = capName;

                _actionQueue.Enqueue(() => CombineElements(doc, localFootingName, localFoundationName, localWallName, localWingLeftName, localWingRightName, localCapName, "A1"));
            }

            // 3. Process superstructure elements and add actions
            foreach (SparqlResult result in resultsSuperstructure)
            {
                string slabName = result["slabName"].ToString().Split('^')[0];
                string protectivewallLeftName = result["protectivewallLeftName"].ToString().Split('^')[0];
                string protectivewallRightName = result["protectivewallRightName"].ToString().Split('^')[0];

                // Create local variables to avoid closure issues
                string localSlabName = slabName;
                string localProtectivewallLeftName = protectivewallLeftName;
                string localProtectivewallRightName = protectivewallRightName;
                string localWallNameA1 = wallNameA1; // Copy wallNameA1 to local variable

                _actionQueue.Enqueue(() =>
                {
                    Element slab = FindElementByName(doc, localSlabName);
                    Element wallA1 = FindElementByName(doc, localWallNameA1);

                    if (slab != null && wallA1 != null)
                    {
                        CombineSlabWithWall(doc, slab, wallA1);
                    }

                    // Move protective walls to slab position
                    Element protectiveWallLeft = FindElementByName(doc, localProtectivewallLeftName);
                    Element protectiveWallRight = FindElementByName(doc, localProtectivewallRightName);
                    if (slab != null && protectiveWallLeft != null && protectiveWallRight != null)
                    {
                        MoveProtectiveWallsToSlab(doc, slab, protectiveWallLeft, protectiveWallRight);
                    }
                });
            }

            // 4. Process Pier results and add actions
            foreach (SparqlResult result in resultsPier)
            {
                string copingName = result["copingName"].ToString().Split('^')[0];
                string columnName = result["columnName"].ToString().Split('^')[0];
                string foundationName = result["foundationName"].ToString().Split('^')[0];
                string footingName = result["footingName"].ToString().Split('^')[0];

                // Create local variables to avoid closure issues
                string localCopingName = copingName;
                string localColumnName = columnName;
                string localFoundationName = foundationName;
                string localFootingName = footingName;

                _actionQueue.Enqueue(() =>
                {
                    // Find superstructure elements
                    Element slab = FindElementByName(doc, "Slab");
                    Element protectiveWallLeft = FindElementByName(doc, "ProtectiveWall_Left");
                    Element protectiveWallRight = FindElementByName(doc, "ProtectiveWall_Right");

                    // Move Pier column relative to slab
                    if (slab != null && localColumnName != null)
                    {
                        Element column = FindElementByName(doc, localColumnName);
                        MovePierColumnToSlab(doc, slab, column);

                        // Set Pier column height
                        SetElementHeightToZero(doc, column);

                        // Find each pier element
                        Element foundation = FindElementByName(doc, localFoundationName);
                        Element footing = FindElementByName(doc, localFootingName);

                        // Combine other elements relative to column
                        if (column != null)
                        {
                            // Pier column and Pier foundation combination
                            if (foundation != null)
                            {
                                CombineFoundationWithPierColumn(doc, foundation, column);
                                SetElementHeightToZero(doc, foundation);
                            }

                            // Pier foundation and Pier footing combination
                            if (foundation != null && footing != null)
                            {
                                CombineFootingWithFoundation(doc, foundation, footing);
                                SetElementHeightToZero(doc, footing);
                            }
                        }
                    }
                });
            }

            // 5. Unhide superstructure elements
            _actionQueue.Enqueue(() =>
            {
                doc.ActiveView.UnhideElements(superstructureElementIds);
            });
        }

        private void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            UIApplication uiApp = _commandData.Application;

            if (_actionQueue.Count > 0)
            {
                DateTime currentTime = DateTime.Now;
                if ((currentTime - _lastActionTime) >= _delay)
                {
                    Action action = _actionQueue.Dequeue();
                    try
                    {
                        UIDocument uidoc = uiApp.ActiveUIDocument;
                        Document doc = uidoc.Document;

                        using (Transaction trans = new Transaction(doc, "Combine Bridge Elements"))
                        {
                            trans.Start();
                            action.Invoke();
                            trans.Commit();
                        }

                        _lastActionTime = DateTime.Now;
                    }
                    catch (Exception ex)
                    {
                        TaskDialog.Show("Error", $"Error occurred during action execution: {ex.Message}");
                        _actionQueue.Clear(); // Clear queue, stop processing
                        uiApp.Idling -= OnIdling;
                        _isProcessing = false;
                    }
                }
            }
            else
            {
                uiApp.Idling -= OnIdling;
                _isProcessing = false;
                TaskDialog.Show("Info", "Abutment, upper structure and Pier combination completed.");
            }
        }

        private void CombinePierElements(Document doc, string footingName, string foundationName, string columnName, string copingName)
        {
            var footing = FindElementByName(doc, footingName);
            var foundation = FindElementByName(doc, foundationName);
            var column = FindElementByName(doc, columnName);
            var coping = FindElementByName(doc, copingName);

            if (footing != null && foundation != null)
            {
                XYZ foundationLocation = new XYZ(GetElementCenter(footing).X, GetElementCenter(footing).Y, GetBoundingBoxMaxPoint(footing).Z + GetHeight(foundation) / 2);
                MoveElementToLocation(foundation, foundationLocation);
            }

            if (column != null)
            {
                MoveColumnToFoundationTop(doc, foundation, column);
            }

            if (coping != null && column != null)
            {
                XYZ copingLocation = new XYZ(GetElementCenter(column).X, GetElementCenter(column).Y, GetBoundingBoxMaxPoint(column).Z + GetHeight(coping) / 2);
                MoveElementToLocation(coping, copingLocation);
            }
        }

        private void MoveColumnToFoundationTop(Document doc, Element foundation, Element column)
        {
            // Calculate foundation X-axis center
            double foundationCenterX = (GetBoundingBoxMinPoint(foundation).X + GetBoundingBoxMaxPoint(foundation).X) / 2;

            // Keep existing code for foundation Y and Z axes
            double foundationCenterY = GetBoundingBoxMinPoint(foundation).Y;
            double foundationTopZ = GetBoundingBoxMaxPoint(foundation).Z;

            // Calculate column bottom center point
            XYZ columnBottomCenter = new XYZ(
                GetBoundingBoxMinPoint(column).X,
                GetBoundingBoxMinPoint(column).Y,
                GetBoundingBoxMinPoint(column).Z
            );

            // Calculate vector to move column to foundation center
            XYZ translation = new XYZ(
                foundationCenterX - columnBottomCenter.X,  // X-axis translation: column to foundation center
                foundationCenterY - columnBottomCenter.Y,  // Y-axis translation: keep existing
                foundationTopZ - columnBottomCenter.Z      // Z-axis translation: keep existing
            );

            // Move column
            ElementTransformUtils.MoveElement(doc, column.Id, translation);
        }

        private void CombineElements(Document doc, string footingName, string foundationName, string wallName, string wingLeftName, string wingRightName, string capName, string abutmentId)
        {
            var footing = FindElementByName(doc, footingName);
            var foundation = FindElementByName(doc, foundationName);
            var wall = FindElementByName(doc, wallName);
            var wingLeft = FindElementByName(doc, wingLeftName);
            var wingRight = FindElementByName(doc, wingRightName);
            var cap = FindElementByName(doc, capName);

            if (footing != null && foundation != null)
            {
                XYZ foundationLocation = new XYZ(GetElementCenter(footing).X, GetElementCenter(footing).Y, GetBoundingBoxMaxPoint(footing).Z + GetHeight(foundation) / 2);
                MoveElementToLocation(foundation, foundationLocation);
            }

            if (wall != null)
            {
                MoveWallAndWingsToFoundationTop(doc, foundation, wall, wingLeft, wingRight, abutmentId);
            }

            if (cap != null && wall != null)
            {
                XYZ capLocation = new XYZ(GetElementCenter(wall).X, GetElementCenter(wall).Y, GetBoundingBoxMaxPoint(wall).Z + GetHeight(cap) / 2);
                MoveElementToLocation(cap, capLocation);
            }
        }

        private void CombineSlabWithWall(Document doc, Element slab, Element wall)
        {
            // Get wall base length
            double wallLength = GetBoundingBoxMaxPoint(wall).X - GetBoundingBoxMinPoint(wall).X;

            // Get wall top center point
            XYZ wallTopCenter = GetTopCenterPoint(wall);

            // Get slab bottom center point
            XYZ slabBottomCenter = GetBottomCenterPoint(slab);

            // Calculate translation vector to position slab on wall
            XYZ translation = new XYZ(
                wallTopCenter.X - slabBottomCenter.X - wallLength,
                wallTopCenter.Y - slabBottomCenter.Y,
                wallTopCenter.Z - slabBottomCenter.Z - GetHeight(wall)
            );

            // Move slab
            ElementTransformUtils.MoveElement(doc, slab.Id, translation);
        }

        private void MoveProtectiveWallsToSlab(Document doc, Element slab, Element protectiveWallLeft, Element protectiveWallRight)
        {
            // Slab position info
            XYZ slabMinPoint = GetBoundingBoxMinPoint(slab);
            XYZ slabMaxPoint = GetBoundingBoxMaxPoint(slab);

            // Calculate slab X-axis center
            double slabCenterX = (slabMinPoint.X + slabMaxPoint.X) / 2.0;

            // Move left protective wall position
            if (protectiveWallLeft != null)
            {
                XYZ newLocationLeft = new XYZ(
                    slabCenterX,
                    slabMinPoint.Y + (slabMaxPoint.Y - slabMinPoint.Y) / 2.0,  // Position at slab Y-axis left edge
                    slabMaxPoint.Z    // Z position same as slab top
                );
                MoveElementToLocation(protectiveWallLeft, newLocationLeft);
                SetElementHeightToZero(doc, protectiveWallLeft);
            }

            // Move right protective wall position
            if (protectiveWallRight != null)
            {
                XYZ newLocationRight = new XYZ(
                    slabCenterX,
                    slabMinPoint.Y + (slabMaxPoint.Y - slabMinPoint.Y) / 2.0,  // Position at slab Y-axis right edge
                    slabMaxPoint.Z    // Z position same as slab top
                );
                MoveElementToLocation(protectiveWallRight, newLocationRight);
                SetElementHeightToZero(doc, protectiveWallRight);
            }
        }



        private void MoveColumnToSlab(Document doc, Element slab, Element columnName)
        {
            // Get slab base length
            double slabLength = GetBoundingBoxMaxPoint(slab).X - GetBoundingBoxMinPoint(slab).X;

            // Get wall base length
            double columnLength = GetBoundingBoxMaxPoint(columnName).X - GetBoundingBoxMinPoint(columnName).X;

            // Get slab bottom center point
            XYZ slabBottomCenter = GetBottomCenterPoint(slab);

            // Get wall top center point
            XYZ ColumnTopCenter = GetTopCenterPoint(columnName);

            // Get wall bottom center point
            XYZ ColumnBottomCenter = GetBottomCenterPoint(columnName);

            // Calculate translation vector to position wall below slab
            XYZ translation = new XYZ(
                slabBottomCenter.X - ColumnTopCenter.X + columnLength + slabLength - columnLength * 2 / 5,
                slabBottomCenter.Y - ColumnTopCenter.Y,
                slabBottomCenter.Z - ColumnTopCenter.Z + GetHeight(columnName)
            );

            // Move wall
            ElementTransformUtils.MoveElement(doc, columnName.Id, translation);
        }

        private void SetElementHeightToZero(Document doc, Element element)
        {
            // Set "Height Offset from Level" parameter to 0
            Parameter heightParam = element.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
            if (heightParam != null && !heightParam.IsReadOnly)
            {
                heightParam.Set(0.0);
            }
            else
            {
                TaskDialog.Show("Debug", $"Failed to set height for {element.Name}");
            }
        }

        private XYZ GetTopCenterPoint(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return new XYZ(
                boundingBox.Max.X,
                (boundingBox.Min.Y + boundingBox.Max.Y) / 2,
                boundingBox.Max.Z
            );
        }

        private XYZ GetBottomCenterPoint(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return new XYZ(
                boundingBox.Min.X,
                (boundingBox.Min.Y + boundingBox.Max.Y) / 2,
                boundingBox.Min.Z
            );
        }
        private XYZ GetBottomCenterPoint2(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return new XYZ(
                boundingBox.Max.X,
                (boundingBox.Min.Y + boundingBox.Max.Y) / 2,
                boundingBox.Min.Z
            );
        }

        private void MoveWallAndWingsToFoundationTop(Document doc, Element foundation, Element wall, Element wingLeft, Element wingRight, string abutmentId)
        {
            // 1. Move wall above foundation
            XYZ translation;
            if (abutmentId == "A1")
            {
                // A1: Move wall to left edge of foundation
                XYZ foundationTopLeft = new XYZ(GetBoundingBoxMaxPoint(foundation).X, GetBoundingBoxMinPoint(foundation).Y, GetBoundingBoxMaxPoint(foundation).Z);
                XYZ wallBottomLeft = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                translation = foundationTopLeft - wallBottomLeft;

                // Get wall base length
                double wallLength = GetBoundingBoxMaxPoint(wall).X - GetBoundingBoxMinPoint(wall).X;

                // Move wall right by wall length
                translation = foundationTopLeft - wallBottomLeft - new XYZ(wallLength * 2 / 5, 0, 0);
            }
            else if (abutmentId == "A2")
            {
                // A2: Keep centered as per original code
                XYZ foundationTopCenter = new XYZ(GetBoundingBoxMinPoint(foundation).X, GetBoundingBoxMinPoint(foundation).Y, GetBoundingBoxMaxPoint(foundation).Z);
                XYZ wallBottomCenter = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                translation = foundationTopCenter - wallBottomCenter;
            }
            else
            {
                // Default: center placement (for abutmentId other than A1, A2)
                XYZ foundationTopCenter = new XYZ(GetBoundingBoxMinPoint(foundation).X, GetBoundingBoxMinPoint(foundation).Y, GetBoundingBoxMaxPoint(foundation).Z);
                XYZ wallBottomCenter = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                translation = foundationTopCenter - wallBottomCenter;
            }
            MoveElementToLocation(wall, GetElementCenter(wall) + translation);

            // 2. Align left wing wall to wall
            if (wingLeft != null)
            {
                XYZ wingLeftTranslation = CalculateTranslationForLeftWing(wall, wingLeft, abutmentId);
                MoveElementToLocation(wingLeft, GetElementCenter(wingLeft) + wingLeftTranslation);
            }

            // 3. Align right wing wall to wall
            if (wingRight != null)
            {
                XYZ wingRightTranslation = CalculateTranslationForRightWing(wall, wingRight, abutmentId);
                MoveElementToLocation(wingRight, GetElementCenter(wingRight) + wingRightTranslation);
            }
        }

        private XYZ CalculateTranslationForLeftWing(Element wall, Element wingLeft, string abutmentId)
        {
            XYZ wallCorner, wingLeftCorner;

            if (abutmentId == "A1")
            {
                // A1 case: calculate left corner alignment
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMaxPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingLeftCorner = new XYZ(GetBoundingBoxMinPoint(wingLeft).X, GetBoundingBoxMaxPoint(wingLeft).Y, GetBoundingBoxMinPoint(wingLeft).Z);

                // Get wing wall base length
                double wingLength = GetBoundingBoxMaxPoint(wingLeft).X - GetBoundingBoxMinPoint(wingLeft).X;
                // Get wall base length
                double wallLength = GetBoundingBoxMaxPoint(wall).X - GetBoundingBoxMinPoint(wall).X;

                // Calculate vector to move to left edge of wall
                return wallCorner - wingLeftCorner - new XYZ(wingLength, 0, 0) + new XYZ(wallLength * 2 / 5, 0, 0);

            }
            else
            {
                // A2 case: align left wing wall to left edge of wall
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMaxPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingLeftCorner = new XYZ(GetBoundingBoxMinPoint(wingLeft).X, GetBoundingBoxMaxPoint(wingLeft).Y, GetBoundingBoxMinPoint(wingLeft).Z);

                // Calculate vector to move to left edge of wall
                return wallCorner - wingLeftCorner;
            }
        }

        private XYZ CalculateTranslationForRightWing(Element wall, Element wingRight, string abutmentId)
        {
            XYZ wallCorner, wingRightCorner;

            if (abutmentId == "A1")
            {
                // A1 case: calculate right corner alignment
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingRightCorner = new XYZ(GetBoundingBoxMinPoint(wingRight).X, GetBoundingBoxMinPoint(wingRight).Y, GetBoundingBoxMinPoint(wingRight).Z);

                // Get wall base length
                double wingLength = GetBoundingBoxMaxPoint(wingRight).X - GetBoundingBoxMinPoint(wingRight).X;

                // Get wall base length
                double wallLength = GetBoundingBoxMaxPoint(wall).X - GetBoundingBoxMinPoint(wall).X;

                // Calculate vector to move to right edge of wall
                return wallCorner - wingRightCorner - new XYZ(wingLength, 0, 0) + new XYZ(wallLength * 2 / 5, 0, 0);
            }
            else
            {
                // A2 case: align right wing wall to right edge of wall
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingRightCorner = new XYZ(GetBoundingBoxMinPoint(wingRight).X, GetBoundingBoxMinPoint(wingRight).Y, GetBoundingBoxMinPoint(wingRight).Z);
            }

            // Calculate vector to move to left edge of wall
            return wallCorner - wingRightCorner;
        }

        private void CombineWingWithWall(Document doc, Element wall, Element wing, bool isLeftWing)
        {
            XYZ wallCorner, wingCorner;

            if (isLeftWing)
            {
                // A2 case: align left wing wall to left edge of wall
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMaxPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingCorner = new XYZ(GetBoundingBoxMinPoint(wing).X, GetBoundingBoxMaxPoint(wing).Y, GetBoundingBoxMinPoint(wing).Z);
            }
            else
            {
                // A2 case: align right wing wall to right edge of wall
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingCorner = new XYZ(GetBoundingBoxMinPoint(wing).X, GetBoundingBoxMinPoint(wing).Y, GetBoundingBoxMinPoint(wing).Z);
            }

            // Calculate vector to move to wall edge
            XYZ translation = wallCorner - wingCorner;

            // Move wing wall
            MoveElementToLocation(wing, GetElementCenter(wing) + translation);
        }

        private void CombineFoundationWithWall(Document doc, Element foundation, Element wall)
        {
            // Calculate vector to align wall bottom with foundation top
            XYZ wallBottomRightCorner = GetBottomRightCorner(wall);
            XYZ foundationTopRightCorner = GetTopRightCorner(foundation);

            // Calculate base vector for wall and foundation alignment
            XYZ translation = wallBottomRightCorner - foundationTopRightCorner;

            // Additional X-axis translation
            double wallLength = GetBoundingBoxMinPoint(wall).X - GetBoundingBoxMaxPoint(wall).X;

            // Move foundation
            MoveElementToLocation(foundation, GetElementCenter(foundation) + translation);
        }

        private void CombineFootingWithFoundation(Document doc, Element foundation, Element footing)
        {
            // Get foundation bottom min coordinates (min point, X,Y,Z)
            XYZ foundationBottomLeft = GetBoundingBoxMinPoint(foundation);

            // Get footing top center point
            XYZ foundationBottomRight = GetBoundingBoxMaxPoint(foundation);

            // Calculate translation vector (X,Y: foundation center, Z: foundation bottom)
            XYZ translation = new XYZ(
                (foundationBottomLeft.X + foundationBottomRight.X) / 2,  // X-axis translation
                (foundationBottomLeft.Y + foundationBottomRight.Y) / 2,  // Y-axis translation
                foundationBottomLeft.Z   // Z-axis translation
            );

            // Move footing below foundation
            MoveElementToLocation(footing, translation);
        }




        private void MovePierColumnToSlab(Document doc, Element slab, Element column)
        {
            // Get slab base length
            double slabLength = GetBoundingBoxMaxPoint(slab).X - GetBoundingBoxMinPoint(slab).X;

            // Get pier column base length
            double columnLength = GetBoundingBoxMaxPoint(column).X - GetBoundingBoxMinPoint(column).X;

            // Get slab bottom center point
            XYZ slabBottomCenter = GetBottomCenterPoint(slab);

            // Get pier column top center point
            XYZ columnTopCenter = GetTopCenterPoint(column);

            // Get pier column bottom center point
            XYZ columnBottomCenter = GetBottomCenterPoint(column);

            // Calculate translation vector to position pier column below slab
            XYZ translation = new XYZ(
                slabBottomCenter.X - columnTopCenter.X + columnLength + slabLength - columnLength * 2 / 5,
                slabBottomCenter.Y - columnTopCenter.Y,
                slabBottomCenter.Z - columnTopCenter.Z + GetHeight(column)
            );

            // Move pier column
            ElementTransformUtils.MoveElement(doc, column.Id, translation);
        }

        private void CombineFoundationWithPierColumn(Document doc, Element foundation, Element column)
        {
            // Calculate vector to align pier column bottom with foundation top
            XYZ columnBottomRightCorner = GetElementCenter(column);
            XYZ foundationTopRightCorner = GetElementCenter(foundation);

            // Calculate base vector to align pier column with foundation
            XYZ translation = columnBottomRightCorner - foundationTopRightCorner;

            // Move foundation
            MoveElementToLocation(foundation, GetElementCenter(foundation) + translation);
        }


        private Element FindElementByName(Document doc, string name)
        {
            var elements = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType().ToElements();
            foreach (Element element in elements)
            {
                if (element.Name.Equals(name))
                {
                    return element;
                }
            }
            return null;
        }

        private XYZ GetBottomRightCorner(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return new XYZ(
                boundingBox.Min.X,
                boundingBox.Min.Y,
                boundingBox.Min.Z
            );
        }

        private XYZ GetTopRightCorner(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return new XYZ(
                boundingBox.Min.X,
                boundingBox.Min.Y,
                boundingBox.Max.Z
            );
        }

        private XYZ GetBoundingBoxMinPoint2(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return boundingBox.Min;
        }

        private XYZ GetBoundingBoxMaxPoint2(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return boundingBox.Max;
        }
        private XYZ GetBoundingBoxMinPoint(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return boundingBox.Min;
        }

        private XYZ GetBoundingBoxMaxPoint(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return boundingBox.Max;
        }

        private double GetHeight(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return boundingBox.Max.Z - boundingBox.Min.Z;
        }

        private void MoveElementToLocation(Element element, XYZ newLocation)
        {
            XYZ elementCenter = GetElementCenter(element);
            XYZ translation = newLocation - elementCenter;
            ElementTransformUtils.MoveElement(element.Document, element.Id, translation);
        }

        private XYZ GetElementCenter(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return (boundingBox.Min + boundingBox.Max) / 2.0;
        }
    }


    [Transaction(TransactionMode.Manual)]
    public class CombineBridgeElements_Pier_Pier : IExternalCommand
    {
        private ExternalCommandData _commandData;
        private static Queue<Action> _actionQueue = new Queue<Action>();
        private static bool _isProcessing = false;
        private static DateTime _lastActionTime = DateTime.MinValue;
        private static readonly TimeSpan _delay = TimeSpan.FromMilliseconds(500); // 0.5 second delay

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Reset static state from previous runs
            _actionQueue.Clear();
            _isProcessing = false;

            _commandData = commandData;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            // Load ontology file and execute SPARQL query
            string ttlFilePath = @"C:\Users\chw42\source\repos\RevitBridge2\RevitBridge2\bin\Debug\WBS_Bridge.ttl";
            Graph g = new Graph();
            try
            {
                FileLoader.Load(g, ttlFilePath);
            }
            catch (Exception ex)
            {
                message = $"Ontology file loading failed: {ex.Message}";
                return Result.Failed;
            }

            // SPARQL query creation and execution
            string querySuperstructure = @"
PREFIX bso: <https://hyunwoochacha.github.io/SO-ASAS/ontology#>
PREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> 
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>

SELECT ?slabName ?protectivewallLeftName ?protectivewallRightName WHERE {
    bso:Slab_Instance bso:name ?slabName .
    bso:Protectivewall_Left_Instance bso:name ?protectivewallLeftName .
    bso:Protectivewall_Right_Instance bso:name ?protectivewallRightName .
}";

            // A1 Pier query
            string queryPierA1 = @"
PREFIX bso: <https://hyunwoochacha.github.io/SO-ASAS/ontology#>
PREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#>
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>

SELECT ?copingName ?columnName ?foundationName ?footingName WHERE {
    bso:A1_PierCoping_Instance bso:name ?copingName .
    bso:A1_PierColumn_Instance bso:name ?columnName .
    bso:A1_PierFoundation_Instance bso:name ?foundationName .
    bso:A1_PierFooting_Instance bso:name ?footingName .
}";

            // A2 Pier query
            string queryPierA2 = @"
PREFIX bso: <https://hyunwoochacha.github.io/SO-ASAS/ontology#>
PREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#>
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>

SELECT ?copingName ?columnName ?foundationName ?footingName WHERE {
    bso:A2_PierCoping_Instance bso:name ?copingName .
    bso:A2_PierColumn_Instance bso:name ?columnName .
    bso:A2_PierFoundation_Instance bso:name ?foundationName .
    bso:A2_PierFooting_Instance bso:name ?footingName .
}";
            // Execute SPARQL query and process results (superstructure)
            SparqlResultSet resultsSuperstructure = (SparqlResultSet)g.ExecuteQuery(querySuperstructure);
            if (resultsSuperstructure == null || resultsSuperstructure.Count == 0)
            {
                message = "SPARQL query failed (superstructure)";
                return Result.Failed;
            }

            // Execute SPARQL query and process results (A1 Pier)
            SparqlResultSet resultsPierA1 = (SparqlResultSet)g.ExecuteQuery(queryPierA1);
            if (resultsPierA1 == null || resultsPierA1.Count == 0)
            {
                message = "SPARQL query failed (A1 Pier)";
                return Result.Failed;
            }

            // Execute SPARQL query and process results (A2 Pier)
            SparqlResultSet resultsPierA2 = (SparqlResultSet)g.ExecuteQuery(queryPierA2);
            if (resultsPierA2 == null || resultsPierA2.Count == 0)
            {
                message = "SPARQL query failed (A2 Pier)";
                return Result.Failed;
            }

            // Add tasks to action queue
            EnqueueActions(doc, resultsSuperstructure, resultsPierA1, resultsPierA2);

            if (_actionQueue.Count == 0)          // No combination actions to execute
            {
                message = "Pier-Pier: No targets to combine.";
                return Result.Failed;             // Exit immediately
            }

            // Register Idling event and start processing
            if (!_isProcessing && _actionQueue.Count > 0)
            {
                UIApplication uiApp = _commandData.Application;
                uiApp.Idling += OnIdling;
                _isProcessing = true;
                _lastActionTime = DateTime.Now;
            }

            return Result.Succeeded;
        }

        private void EnqueueActions(Document doc, SparqlResultSet resultsSuperstructure, SparqlResultSet resultsPierA1, SparqlResultSet resultsPierA2)
        {
            // List to store superstructure ElementIds
            List<ElementId> superstructureElementIds = new List<ElementId>();

            // 1. Hide superstructure elements
            _actionQueue.Enqueue(() =>
            {
                foreach (SparqlResult result in resultsSuperstructure)
                {
                    string slabName = result["slabName"].ToString().Split('^')[0];
                    string protectivewallLeftName = result["protectivewallLeftName"].ToString().Split('^')[0];
                    string protectivewallRightName = result["protectivewallRightName"].ToString().Split('^')[0];

                    // Create local variables
                    string localSlabName = slabName;
                    string localProtectivewallLeftName = protectivewallLeftName;
                    string localProtectivewallRightName = protectivewallRightName;

                    // Find elements
                    Element slab = FindElementByName(doc, localSlabName);
                    Element protectiveWallLeft = FindElementByName(doc, localProtectivewallLeftName);
                    Element protectiveWallRight = FindElementByName(doc, localProtectivewallRightName);

                    // Collect superstructure element IDs
                    if (slab != null) superstructureElementIds.Add(slab.Id);
                    if (protectiveWallLeft != null) superstructureElementIds.Add(protectiveWallLeft.Id);
                    if (protectiveWallRight != null) superstructureElementIds.Add(protectiveWallRight.Id);
                }

                // Hide elements
                if (superstructureElementIds.Count > 0)
                    doc.ActiveView.HideElements(superstructureElementIds);
            });

            // 2. Process A1 Pier and add actions
            foreach (SparqlResult result in resultsPierA1)
            {
                string footingName = result["footingName"].ToString().Split('^')[0];
                string foundationName = result["foundationName"].ToString().Split('^')[0];
                string columnName = result["columnName"].ToString().Split('^')[0];
                string copingName = result["copingName"].ToString().Split('^')[0];

                // Create local variables
                string localFootingName = footingName;
                string localFoundationName = foundationName;
                string localColumnName = columnName;
                string localCopingName = copingName;

                _actionQueue.Enqueue(() =>
                {
                    // Combine pier elements
                    CombineElements(doc, localFootingName, localFoundationName, localColumnName, localCopingName);

                    // Slab and column combination
                    Element slab = FindElementByName(doc, "Slab");
                    if (slab != null && localColumnName != null)
                    {
                        Element column = FindElementByName(doc, localColumnName);
                        CombineSlabWithColumn(doc, slab, column);
                    }
                });
            }

            // 3. Process superstructure elements and add actions
            foreach (SparqlResult result in resultsSuperstructure)
            {
                string slabName = result["slabName"].ToString().Split('^')[0];
                string protectivewallLeftName = result["protectivewallLeftName"].ToString().Split('^')[0];
                string protectivewallRightName = result["protectivewallRightName"].ToString().Split('^')[0];

                // Create local variables
                string localSlabName = slabName;
                string localProtectivewallLeftName = protectivewallLeftName;
                string localProtectivewallRightName = protectivewallRightName;

                _actionQueue.Enqueue(() =>
                {
                    Element slab = FindElementByName(doc, localSlabName);
                    Element protectiveWallLeft = FindElementByName(doc, localProtectivewallLeftName);
                    Element protectiveWallRight = FindElementByName(doc, localProtectivewallRightName);

                    // Move protective walls to slab position
                    if (slab != null && protectiveWallLeft != null && protectiveWallRight != null)
                    {
                        MoveProtectiveWallsToSlab(doc, slab, protectiveWallLeft, protectiveWallRight);
                    }
                });
            }

            // 4. Process A2 Pier and add actions
            foreach (SparqlResult result in resultsPierA2)
            {
                string footingName = result["footingName"].ToString().Split('^')[0];
                string foundationName = result["foundationName"].ToString().Split('^')[0];
                string columnName = result["columnName"].ToString().Split('^')[0];
                string copingName = result["copingName"].ToString().Split('^')[0];

                // Create local variables
                string localFootingName = footingName;
                string localFoundationName = foundationName;
                string localColumnName = columnName;
                string localCopingName = copingName;

                _actionQueue.Enqueue(() =>
                {
                    // Move A2 wall relative to slab
                    Element slab = FindElementByName(doc, "Slab");
                    if (slab != null && localColumnName != null)
                    {
                        Element wallA2 = FindElementByName(doc, localColumnName);
                        MoveWallToSlab(doc, slab, wallA2);

                        // Set wall height
                        SetElementHeightToZero(doc, wallA2);

                        // Find each element of A2 abutment
                        Element foundationA2 = FindElementByName(doc, localFoundationName);
                        Element footingA2 = FindElementByName(doc, localFootingName);

                        // Wall and abutment combination
                        if (wallA2 != null)
                        {
                            if (foundationA2 != null)
                            {
                                CombineFoundationWithWall(doc, foundationA2, wallA2);
                                SetElementHeightToZero(doc, foundationA2);
                            }

                            if (foundationA2 != null && footingA2 != null)
                            {
                                CombineFootingWithFoundation(doc, foundationA2, footingA2);
                                SetElementHeightToZero(doc, footingA2);
                            }
                        }
                    }
                });
            }

            // 5. Unhide superstructure elements
            _actionQueue.Enqueue(() =>
            {
                doc.ActiveView.UnhideElements(superstructureElementIds);
            });
        }

        private void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            UIApplication uiApp = _commandData.Application;

            if (_actionQueue.Count > 0)
            {
                DateTime currentTime = DateTime.Now;
                if ((currentTime - _lastActionTime) >= _delay)
                {
                    Action action = _actionQueue.Dequeue();
                    try
                    {
                        UIDocument uidoc = uiApp.ActiveUIDocument;
                        Document doc = uidoc.Document;

                        using (Transaction trans = new Transaction(doc, "Combine Bridge Elements"))
                        {
                            trans.Start();
                            action.Invoke();
                            trans.Commit();
                        }

                        _lastActionTime = DateTime.Now;
                    }
                    catch (Exception ex)
                    {
                        TaskDialog.Show("Error", $"Error occurred during action execution: {ex.Message}");
                        _actionQueue.Clear(); // Clear queue, stop processing
                        uiApp.Idling -= OnIdling;
                        _isProcessing = false;
                    }
                }
            }
            else
            {
                uiApp.Idling -= OnIdling;
                _isProcessing = false;
                TaskDialog.Show("Info", "Pier and upper structure combination completed.");
            }
        }

        private void CombineElements(Document doc, string footingName, string foundationName, string columnName, string copingName)
        {
            var footing = FindElementByName(doc, footingName);
            var foundation = FindElementByName(doc, foundationName);
            var column = FindElementByName(doc, columnName);
            var coping = FindElementByName(doc, copingName);

            if (footing != null && foundation != null)
            {
                XYZ foundationLocation = new XYZ(GetElementCenter(footing).X, GetElementCenter(footing).Y, GetBoundingBoxMaxPoint(footing).Z + GetHeight(foundation) / 2);
                MoveElementToLocation(foundation, foundationLocation);
            }

            if (column != null)
            {
                MoveColumnToFoundationTop(doc, foundation, column);
            }

            if (coping != null && column != null)
            {
                XYZ copingLocation = new XYZ(GetElementCenter(column).X, GetElementCenter(column).Y, GetBoundingBoxMaxPoint(column).Z + GetHeight(coping) / 2);
                MoveElementToLocation(coping, copingLocation);
            }
        }

        private void CombineSlabWithColumn(Document doc, Element slab, Element column)
        {
            // Get column base length
            double columnLength = GetBoundingBoxMaxPoint(column).X - GetBoundingBoxMinPoint(column).X;

            // Get column top center point
            XYZ columnTopCenter = GetTopCenterPoint(column);

            // Get slab bottom center point
            XYZ slabBottomCenter = GetBottomCenterPoint(slab);

            // Calculate translation vector to position slab above column
            XYZ translation = new XYZ(
                columnTopCenter.X - slabBottomCenter.X - columnLength,
                columnTopCenter.Y - slabBottomCenter.Y,
                columnTopCenter.Z - slabBottomCenter.Z - GetHeight(column)
            );

            // Move slab
            ElementTransformUtils.MoveElement(doc, slab.Id, translation);
        }

        private void MoveProtectiveWallsToSlab(Document doc, Element slab, Element protectiveWallLeft, Element protectiveWallRight)
        {
            // Slab position info
            XYZ slabMinPoint = GetBoundingBoxMinPoint(slab);
            XYZ slabMaxPoint = GetBoundingBoxMaxPoint(slab);

            // Calculate slab X-axis center
            double slabCenterX = (slabMinPoint.X + slabMaxPoint.X) / 2.0;

            // Move left protective wall position
            if (protectiveWallLeft != null)
            {
                XYZ newLocationLeft = new XYZ(
                    slabCenterX,
                    slabMinPoint.Y + (slabMaxPoint.Y - slabMinPoint.Y) / 2.0,  // Position at slab Y-axis left edge
                    slabMaxPoint.Z    // Z position same as slab top
                );
                MoveElementToLocation(protectiveWallLeft, newLocationLeft);
                SetElementHeightToZero(doc, protectiveWallLeft);
            }

            // Move right protective wall position
            if (protectiveWallRight != null)
            {
                XYZ newLocationRight = new XYZ(
                    slabCenterX,
                    slabMinPoint.Y + (slabMaxPoint.Y - slabMinPoint.Y) / 2.0,  // Position at slab Y-axis right edge
                    slabMaxPoint.Z    // Z position same as slab top
                );
                MoveElementToLocation(protectiveWallRight, newLocationRight);
                SetElementHeightToZero(doc, protectiveWallRight);
            }
        }

        private void MoveWallToSlab(Document doc, Element slab, Element wall)
        {
            // Get slab base length
            double slabLength = GetBoundingBoxMaxPoint(slab).X - GetBoundingBoxMinPoint(slab).X;

            // Get wall base length
            double wallLength = GetBoundingBoxMaxPoint(wall).X - GetBoundingBoxMinPoint(wall).X;

            // Get slab bottom center point
            XYZ slabBottomCenter = GetBottomCenterPoint(slab);

            // Get wall top center point
            XYZ wallTopCenter = GetTopCenterPoint(wall);

            // Get wall bottom center point
            XYZ wallBottomCenter = GetBottomCenterPoint(wall);

            // Calculate translation vector to position wall below slab
            XYZ translation = new XYZ(
                slabBottomCenter.X - wallTopCenter.X + wallLength + slabLength - wallLength * 2 / 5,
                slabBottomCenter.Y - wallTopCenter.Y,
                slabBottomCenter.Z - wallTopCenter.Z + GetHeight(wall)
            );

            // Move wall
            ElementTransformUtils.MoveElement(doc, wall.Id, translation);
        }

        private void SetElementHeightToZero(Document doc, Element element)
        {
            // Set "Height Offset from Level" parameter to 0
            Parameter heightParam = element.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
            if (heightParam != null && !heightParam.IsReadOnly)
            {
                heightParam.Set(0.0);
            }
        }

        private XYZ GetTopCenterPoint(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return new XYZ(
                boundingBox.Max.X,
                (boundingBox.Min.Y + boundingBox.Max.Y) / 2,
                boundingBox.Max.Z
            );
        }

        private XYZ GetBottomCenterPoint(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return new XYZ(
                boundingBox.Min.X,
                (boundingBox.Min.Y + boundingBox.Max.Y) / 2,
                boundingBox.Min.Z
            );
        }

        private XYZ GetBottomCenterPoint2(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return new XYZ(
                boundingBox.Max.X,
                (boundingBox.Min.Y + boundingBox.Max.Y) / 2,
                boundingBox.Min.Z
            );
        }

        private void MoveWallAndWingsToFoundationTop(Document doc, Element foundation, Element wall, string abutmentId)
        {
            // 1. Move wall above foundation
            XYZ translation;
            if (abutmentId == "A1")
            {
                // A1: Move wall to left edge of foundation
                XYZ foundationTopLeft = new XYZ(GetBoundingBoxMaxPoint(foundation).X, GetBoundingBoxMinPoint(foundation).Y, GetBoundingBoxMaxPoint(foundation).Z);
                XYZ wallBottomLeft = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                translation = foundationTopLeft - wallBottomLeft;

                // Get wall base length
                double wallLength = GetBoundingBoxMaxPoint(wall).X - GetBoundingBoxMinPoint(wall).X;

                // Move wall right by wall length
                translation = foundationTopLeft - wallBottomLeft - new XYZ(wallLength * 2 / 5, 0, 0);
            }
            else if (abutmentId == "A2")
            {
                // A1: Move wall to left edge of foundation
                XYZ foundationTopLeft = new XYZ(GetBoundingBoxMaxPoint(foundation).X, GetBoundingBoxMinPoint(foundation).Y, GetBoundingBoxMaxPoint(foundation).Z);
                XYZ wallBottomLeft = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                translation = foundationTopLeft - wallBottomLeft;

                // Get wall base length
                double wallLength = GetBoundingBoxMaxPoint(wall).X - GetBoundingBoxMinPoint(wall).X;

                // Move wall right by wall length
                translation = foundationTopLeft - wallBottomLeft - new XYZ(wallLength * 2 / 5, 0, 0);
            }
        }

        private XYZ CalculateTranslationForLeftWing(Element wall, Element wingLeft, string abutmentId)
        {
            XYZ wallCorner, wingLeftCorner;

            if (abutmentId == "A1")
            {
                // A1 case: calculate left corner alignment
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMaxPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingLeftCorner = new XYZ(GetBoundingBoxMinPoint(wingLeft).X, GetBoundingBoxMaxPoint(wingLeft).Y, GetBoundingBoxMinPoint(wingLeft).Z);

                // Get wing wall base length
                double wingLength = GetBoundingBoxMaxPoint(wingLeft).X - GetBoundingBoxMinPoint(wingLeft).X;
                // Get wall base length
                double wallLength = GetBoundingBoxMaxPoint(wall).X - GetBoundingBoxMinPoint(wall).X;

                // Calculate vector to move to left edge of wall
                return wallCorner - wingLeftCorner - new XYZ(wingLength, 0, 0) + new XYZ(wallLength * 2 / 5, 0, 0);
            }
            else
            {
                // A2: Calculate to align left wing wall to left edge of wall
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMaxPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingLeftCorner = new XYZ(GetBoundingBoxMinPoint(wingLeft).X, GetBoundingBoxMaxPoint(wingLeft).Y, GetBoundingBoxMinPoint(wingLeft).Z);

                // Calculate vector to move to left edge of wall
                return wallCorner - wingLeftCorner;
            }
        }

        private XYZ CalculateTranslationForRightWing(Element wall, Element wingRight, string abutmentId)
        {
            XYZ wallCorner, wingRightCorner;

            if (abutmentId == "A1")
            {
                // A1 case: calculate right corner alignment
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingRightCorner = new XYZ(GetBoundingBoxMinPoint(wingRight).X, GetBoundingBoxMinPoint(wingRight).Y, GetBoundingBoxMinPoint(wingRight).Z);

                // Get wall base length
                double wingLength = GetBoundingBoxMaxPoint(wingRight).X - GetBoundingBoxMinPoint(wingRight).X;

                // Get wall base length
                double wallLength = GetBoundingBoxMaxPoint(wall).X - GetBoundingBoxMinPoint(wall).X;

                // Calculate vector to move to right edge of wall
                return wallCorner - wingRightCorner - new XYZ(wingLength, 0, 0) + new XYZ(wallLength * 2 / 5, 0, 0);
            }
            else
            {
                // A2 case: align right wing wall to right edge of wall
                wallCorner = new XYZ(GetBoundingBoxMinPoint(wall).X, GetBoundingBoxMinPoint(wall).Y, GetBoundingBoxMinPoint(wall).Z);
                wingRightCorner = new XYZ(GetBoundingBoxMinPoint(wingRight).X, GetBoundingBoxMinPoint(wingRight).Y, GetBoundingBoxMinPoint(wingRight).Z);
            }

            // Calculate vector to move to left edge of wall
            return wallCorner - wingRightCorner;
        }

        private void CombineFoundationWithWall(Document doc, Element foundation, Element wall)
        {
            // Calculate vector to align wall bottom with foundation top
            XYZ wallBottomRightCorner = GetElementCenter(wall);
            XYZ foundationTopRightCorner = GetElementCenter(foundation);

            // Calculate base vector for wall and foundation alignment
            XYZ translation = wallBottomRightCorner - foundationTopRightCorner;

            // Move foundation
            MoveElementToLocation(foundation, GetElementCenter(foundation) + translation);
        }

        private void CombineFootingWithFoundation(Document doc, Element foundation, Element footing)
        {
            // Get foundation bottom min coordinates (min point, X,Y,Z)
            XYZ foundationBottomLeft = GetBoundingBoxMinPoint(foundation);

            // Get footing top center point
            XYZ foundationBottomRight = GetBoundingBoxMaxPoint(foundation);

            // Calculate translation vector (X,Y: foundation center, Z: foundation bottom)
            XYZ translation = new XYZ(
                (foundationBottomLeft.X + foundationBottomRight.X) / 2,  // X-axis translation
                (foundationBottomLeft.Y + foundationBottomRight.Y) / 2,  // Y-axis translation
                foundationBottomLeft.Z    // Z-axis translation
            );

            // Move footing below foundation
            MoveElementToLocation(footing, translation);
        }

        private void MoveColumnToFoundationTop(Document doc, Element foundation, Element column)
        {
            // Calculate foundation X-axis center
            double foundationCenterX = (GetBoundingBoxMinPoint(foundation).X + GetBoundingBoxMaxPoint(foundation).X) / 2;

            // Keep existing code for foundation Y and Z axes
            double foundationCenterY = GetBoundingBoxMinPoint(foundation).Y;
            double foundationTopZ = GetBoundingBoxMaxPoint(foundation).Z;

            // Calculate column bottom center point
            XYZ columnBottomCenter = new XYZ(
                GetBoundingBoxMinPoint(column).X,
                GetBoundingBoxMinPoint(column).Y,
                GetBoundingBoxMinPoint(column).Z
            );

            // Calculate vector to move column to foundation center
            XYZ translation = new XYZ(
                foundationCenterX - columnBottomCenter.X,  // X-axis translation: column to foundation center
                foundationCenterY - columnBottomCenter.Y,  // Y-axis translation: keep existing
                foundationTopZ - columnBottomCenter.Z      // Z-axis translation: keep existing
            );

            // Move column
            ElementTransformUtils.MoveElement(doc, column.Id, translation);
        }

        private Element FindElementByName(Document doc, string name)
        {
            var elements = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .ToElements();

            foreach (var element in elements)
            {
                if (element.Name.Equals(name))
                {
                    return element;
                }
            }

            return null;
        }

        private XYZ GetBottomRightCorner(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return new XYZ(
                boundingBox.Min.X,
                boundingBox.Min.Y,
                boundingBox.Min.Z
            );
        }

        private XYZ GetTopRightCorner(Element element)
        {
            BoundingBoxXYZ boundingBox = element.get_BoundingBox(null);
            return new XYZ(
                boundingBox.Min.X,
                boundingBox.Min.Y,
                boundingBox.Max.Z
            );
        }

        private XYZ GetBoundingBoxMinPoint(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return boundingBox.Min;
        }

        private XYZ GetBoundingBoxMaxPoint(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return boundingBox.Max;
        }

        private double GetHeight(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return boundingBox.Max.Z - boundingBox.Min.Z;
        }

        private void MoveElementToLocation(Element element, XYZ newLocation)
        {
            XYZ elementCenter = GetElementCenter(element);
            XYZ translation = newLocation - elementCenter;
            ElementTransformUtils.MoveElement(element.Document, element.Id, translation);
        }

        private XYZ GetElementCenter(Element element)
        {
            var boundingBox = element.get_BoundingBox(null);
            return (boundingBox.Min + boundingBox.Max) / 2.0;
        }
    }

}



