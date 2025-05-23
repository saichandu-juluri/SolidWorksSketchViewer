﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksSketchViewer.Models;
using SolidWorksSketchViewer.Services;

namespace SolidWorksSketchViewer.Helpers
{
    /// <summary>
    /// Main service class for all SolidWorks API operations
    /// This is where all backend SolidWorks logic should be implemented
    /// </summary>
    public class SolidWorksService : IDisposable
    {
        private SldWorks swApp;
        private ModelDoc2 swModel;
        private AssemblyDoc swAssembly;

        // Store original values for potential rollback
        private Dictionary<string, object> originalValues = new Dictionary<string, object>();

        #region Initialization and Cleanup

        public SolidWorksService()
        {
            try
            {
                // Try to connect to running instance
                swApp = (SldWorks)Marshal.GetActiveObject("SldWorks.Application");
            }
            catch
            {
                // Create new instance if not running
                swApp = new SldWorks();
                swApp.Visible = true;
            }
        }

        public void Dispose()
        {
            CloseDocument();
            if (swApp != null)
            {
                Marshal.ReleaseComObject(swApp);
                swApp = null;
            }
        }

        #endregion

        #region Assembly Operations

        /// <summary>
        /// Opens a SolidWorks assembly and returns its metadata
        /// </summary>
        public AssemblyInfo OpenAssembly(string assemblyPath)
        {
            try
            {
                if (!File.Exists(assemblyPath))
                    throw new FileNotFoundException($"Assembly file not found: {assemblyPath}");

                // Close any open document
                CloseDocument();

                // Open the assembly
                int errors = 0;
                int warnings = 0;
                swModel = swApp.OpenDoc6(
                    assemblyPath,
                    (int)swDocumentTypes_e.swDocASSEMBLY,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                    "",
                    ref errors,
                    ref warnings
                );

                if (swModel == null)
                    throw new Exception($"Failed to open assembly. Errors: {errors}, Warnings: {warnings}");

                swAssembly = (AssemblyDoc)swModel;

                // Gather assembly information
                var assemblyInfo = new AssemblyInfo
                {
                    Name = Path.GetFileName(assemblyPath),
                    FilePath = assemblyPath,
                    FileSize = new FileInfo(assemblyPath).Length,
                    PartCount = GetComponentCount(),
                    Features = GetFeatureList(),
                    Sketches = GetSketchList()
                };

                // Try to get thumbnail
                assemblyInfo.ThumbnailPath = ExtractThumbnail(assemblyPath);

                return assemblyInfo;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error opening assembly: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Process all modifications from JSON
        /// </summary>
        public async Task<List<ModificationResult>> ProcessModifications(
            string modificationJson,
            Action<FeatureProcessingStatus> progressCallback)
        {
            var results = new List<ModificationResult>();

            try
            {
                // Parse JSON into modifications
                var modifications = ParseModificationJson(modificationJson);

                foreach (var mod in modifications)
                {
                    var status = new FeatureProcessingStatus
                    {
                        FeatureName = mod.FeatureName,
                        StatusIcon = "⏳",
                        Message = "Processing...",
                        ProcessingTime = "0.0s",
                        BackgroundColor = "#FFF3E0"
                    };

                    progressCallback?.Invoke(status);

                    var startTime = DateTime.Now;
                    ModificationResult result = null;

                    // Process based on modification type
                    switch (mod.Type.ToLower())
                    {
                        case "dimension":
                            result = await ProcessDimensionModification(mod);
                            break;

                        case "material":
                            result = await ProcessMaterialModification(mod);
                            break;

                        case "feature":
                            result = await ProcessFeatureModification(mod);
                            break;

                        default:
                            result = new ModificationResult
                            {
                                FeatureName = mod.FeatureName,
                                Success = false,
                                ErrorMessage = $"Unknown modification type: {mod.Type}"
                            };
                            break;
                    }

                    // Update status based on result
                    status.ProcessingTime = $"{(DateTime.Now - startTime).TotalSeconds:F1}s";

                    if (result.Success)
                    {
                        status.StatusIcon = "✓";
                        status.Message = "Successfully modified";
                        status.BackgroundColor = "#E8F5E9";
                    }
                    else
                    {
                        status.StatusIcon = "❌";
                        status.Message = result.ErrorMessage;
                        status.BackgroundColor = "#FFEBEE";
                    }

                    progressCallback?.Invoke(status);
                    results.Add(result);

                    // Small delay for UI updates
                    await Task.Delay(100);
                }

                // Force rebuild if needed
                swModel.ForceRebuild3(false);

                return results;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error processing modifications: {ex.Message}", ex);
            }
        }

        #endregion

        #region Dimension Modifications

        /// <summary>
        /// Modifies a dimension value
        /// </summary>
        public DimensionModificationResult ModifyDimension(
            string featureName,
            string dimensionName,
            double newValue)
        {
            try
            {
                // Clear selection
                swModel.ClearSelection2(true);

                // Find and select the dimension
                DisplayDimension dispDim = FindDimension(featureName, dimensionName);
                if (dispDim == null)
                {
                    return new DimensionModificationResult
                    {
                        Success = false,
                        ErrorMessage = $"Dimension {dimensionName} not found in {featureName}"
                    };
                }

                // Get the dimension object
                Dimension dim = (Dimension)dispDim.GetDimension();

                // Store original value
                double originalValue = dim.Value;
                string key = $"{featureName}_{dimensionName}";
                originalValues[key] = originalValue;

                // Set new value
                int retval = dim.SetSystemValue3(
                    newValue,
                    (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration,
                    null
                );

                if (retval != (int)swSetValueReturnStatus_e.swSetValue_Successful)
                {
                    return new DimensionModificationResult
                    {
                        Success = false,
                        ErrorMessage = "Failed to set dimension value",
                        OldValue = originalValue,
                        NewValue = newValue
                    };
                }

                // Rebuild to apply changes
                swModel.EditRebuild3();

                return new DimensionModificationResult
                {
                    Success = true,
                    OldValue = originalValue,
                    NewValue = newValue,
                    Units = GetDimensionUnits(dim)
                };
            }
            catch (Exception ex)
            {
                return new DimensionModificationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        private async Task<ModificationResult> ProcessDimensionModification(Modification mod)
        {
            await Task.Delay(100); // Simulate async operation

            var dimResult = ModifyDimension(
                mod.FeatureName,
                mod.DimensionName,
                Convert.ToDouble(mod.NewValue)
            );

            return new ModificationResult
            {
                FeatureName = mod.FeatureName,
                Success = dimResult.Success,
                ErrorMessage = dimResult.ErrorMessage,
                OldValue = dimResult.OldValue.ToString(),
                NewValue = dimResult.NewValue.ToString()
            };
        }

        private async Task<ModificationResult> ProcessMaterialModification(Modification mod)
        {
            await Task.Delay(100); // Simulate async operation

            var matResult = ChangeMaterial(
                mod.FeatureName,
                mod.NewValue?.ToString() ?? ""
            );

            return new ModificationResult
            {
                FeatureName = mod.FeatureName,
                Success = matResult.Success,
                ErrorMessage = matResult.ErrorMessage,
                OldValue = matResult.OldMaterial,
                NewValue = matResult.NewMaterial
            };
        }

        private async Task<ModificationResult> ProcessFeatureModification(Modification mod)
        {
            await Task.Delay(100); // Simulate async operation

            var edges = mod.Parameters?.ContainsKey("edges") == true
                ? mod.Parameters["edges"] as List<string>
                : new List<string>();

            var featureResult = AddFeature(
                mod.Type,
                edges,
                mod.Parameters ?? new Dictionary<string, object>()
            );

            return new ModificationResult
            {
                FeatureName = featureResult.FeatureName ?? mod.FeatureName,
                Success = featureResult.Success,
                ErrorMessage = featureResult.ErrorMessage,
                OldValue = "None",
                NewValue = mod.Type
            };
        }

        #endregion

        #region Material Modifications

        /// <summary>
        /// Changes material of a component
        /// </summary>
        public MaterialChangeResult ChangeMaterial(string partName, string newMaterial)
        {
            try
            {
                // Get the component
                Component2 comp = FindComponent(partName);
                if (comp == null)
                {
                    return new MaterialChangeResult
                    {
                        Success = false,
                        ErrorMessage = $"Component {partName} not found"
                    };
                }

                // Get the model doc of the component
                ModelDoc2 compModel = (ModelDoc2)comp.GetModelDoc2();
                if (compModel == null)
                {
                    return new MaterialChangeResult
                    {
                        Success = false,
                        ErrorMessage = "Could not access component model"
                    };
                }

                // Get current material
                string currentMaterial = compModel.MaterialIdName;

                // Store original value
                originalValues[$"{partName}_Material"] = currentMaterial;

                // Set new material
                compModel.MaterialIdName = newMaterial;

                // Apply material from database
                string materialDB = FindMaterialDatabase(newMaterial);
                if (!string.IsNullOrEmpty(materialDB))
                {
                    // Using PartDoc method for material assignment
                    if (compModel.GetType() == (int)swDocumentTypes_e.swDocPART)
                    {
                        PartDoc partDoc = (PartDoc)compModel;
                        partDoc.SetMaterialPropertyName2(
                            "",  // Configuration name (empty for all configs)
                            materialDB,  // Database path
                            newMaterial  // Material name
                        );
                    }
                }

                return new MaterialChangeResult
                {
                    Success = true,
                    OldMaterial = currentMaterial,
                    NewMaterial = newMaterial
                };
            }
            catch (Exception ex)
            {
                return new MaterialChangeResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        #endregion

        #region Feature Operations

        /// <summary>
        /// Adds a new feature (chamfer, fillet, etc.)
        /// </summary>
        public FeatureAddResult AddFeature(
            string featureType,
            List<string> edges,
            Dictionary<string, object> parameters)
        {
            try
            {
                Feature newFeature = null;

                switch (featureType.ToLower())
                {
                    case "chamfer":
                        newFeature = AddChamferFeature(edges, parameters);
                        break;

                    case "fillet":
                        newFeature = AddFilletFeature(edges, parameters);
                        break;

                    case "hole":
                        newFeature = AddHoleFeature(parameters);
                        break;

                    default:
                        return new FeatureAddResult
                        {
                            Success = false,
                            ErrorMessage = $"Unknown feature type: {featureType}"
                        };
                }

                if (newFeature == null)
                {
                    return new FeatureAddResult
                    {
                        Success = false,
                        ErrorMessage = "Failed to create feature"
                    };
                }

                return new FeatureAddResult
                {
                    Success = true,
                    FeatureName = newFeature.Name
                };
            }
            catch (Exception ex)
            {
                return new FeatureAddResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        private Feature AddChamferFeature(List<string> edges, Dictionary<string, object> parameters)
        {
            // Clear selection
            swModel.ClearSelection2(true);

            // Select edges
            foreach (var edgeName in edges)
            {
                SelectEdge(edgeName);
            }

            // Get chamfer distance
            double distance = 5.0; // Default 5mm
            if (parameters.ContainsKey("distance"))
            {
                distance = Convert.ToDouble(parameters["distance"]);
            }

            // Create chamfer with all required parameters
            swModel.FeatureManager.InsertFeatureChamfer(
                0,                    // Type: 0 for distance-distance
                0,                    // PropagateFlag
                distance / 1000.0,    // Width (convert mm to meters)
                distance / 1000.0,    // D1 (Distance 1)
                0,                    // D2 (Distance 2)
                0,                    // DOverride1
                0,                    // DOverride2
                0                     // VertexChamDist3
            );

            return (Feature)swModel.FeatureByPositionReverse(0);
        }

        private Feature AddFilletFeature(List<string> edges, Dictionary<string, object> parameters)
        {
            // Clear selection
            swModel.ClearSelection2(true);

            // Select edges
            foreach (var edgeName in edges)
            {
                SelectEdge(edgeName);
            }

            // Get fillet radius
            double radius = 5.0; // Default 5mm
            if (parameters.ContainsKey("radius"))
            {
                radius = Convert.ToDouble(parameters["radius"]);
            }

            try
            {
                // Create fillet using FeatureFillet with all required parameters
                // Create arrays for the required parameters
                double[] radiiArray = new double[] { radius / 1000.0 }; // Convert mm to meters
                object radiiObj = radiiArray;

                Feature filletFeature = (Feature)swModel.FeatureManager.FeatureFillet(
                    1,                  // Options: 1 = uniform radius
                    radius / 1000.0,    // Radius (convert mm to meters)
                    0,                  // Number of contours (0 = use selection)
                    0,                  // Feature scope
                    radiiObj,           // Radii array
                    null,               // SetBackDistances
                    null                // PointRadiusArray
                );

                return filletFeature;
            }
            catch
            {
                // If the simple method doesn't work, try the complex one
                try
                {
                    // FeatureFillet3 with all required parameters
                    Feature filletFeature3 = (Feature)swModel.FeatureManager.FeatureFillet3(
                        195,                  // Options
                        radius / 1000.0,      // Default radius
                        0,                    // Fillet type
                        0,                    // Overflow type
                        0,                    // RadType
                        0,                    // UseAutoSelect
                        0,                    // Continuity
                        0,                    // SetBackDistance
                        0,                    // PointRadiusDistance
                        0,                    // CornerType
                        0,                    // ReverseFillet
                        0,                    // VectorReverseFillet
                        0,                    // ReverseSurfaceFillet
                        0                     // VectorReverseSurfaceFillet
                    );

                    return filletFeature3;
                }
                catch
                {
                    return null;
                }
            }
        }

        private Feature AddHoleFeature(Dictionary<string, object> parameters)
        {
            // This is a simplified hole creation
            // In real implementation, you'd need face selection and positioning

            double diameter = 10.0; // Default 10mm
            double depth = 20.0;    // Default 20mm

            if (parameters.ContainsKey("diameter"))
            {
                diameter = Convert.ToDouble(parameters["diameter"]);
            }
            if (parameters.ContainsKey("depth"))
            {
                depth = Convert.ToDouble(parameters["depth"]);
            }

            // Note: This is simplified. Real implementation needs:
            // 1. Select face
            // 2. Define position
            // 3. Create hole wizard feature

            // For now, return null as placeholder
            return null;
        }

        #endregion

        #region Save Operations

        /// <summary>
        /// Saves assembly to new location with all references
        /// </summary>
        public SaveAssemblyResult SaveAssemblyAs(string newPath, bool copyReferencedFiles = true)
        {
            try
            {
                var result = new SaveAssemblyResult
                {
                    SavedFiles = new List<string>()
                };

                // Create directory if it doesn't exist
                string directory = Path.GetDirectoryName(newPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (copyReferencedFiles)
                {
                    // Use Pack and Go for complete assembly copy
                    PackAndGo packAndGo = swModel.Extension.GetPackAndGo();

                    packAndGo.IncludeDrawings = false;
                    packAndGo.IncludeSimulationResults = false;
                    packAndGo.IncludeToolboxComponents = true;
                    packAndGo.IncludeSuppressed = true;

                    // Set destination
                    packAndGo.SetSaveToName(true, directory);

                    // Get all files that will be copied
                    object fileNames;
                    object fileStatus;
                    packAndGo.GetDocumentNames(out fileNames);
                    packAndGo.GetDocumentSaveToNames(out fileNames, out fileStatus);

                    // Perform Pack and Go
                    int[] statuses = (int[])swModel.Extension.SavePackAndGo(packAndGo);

                    string[] savedFiles = (string[])fileNames;
                    result.SavedFiles.AddRange(savedFiles);
                }
                else
                {
                    // Just save the assembly
                    int errors = 0;
                    int warnings = 0;

                    bool success = swModel.Extension.SaveAs(
                        newPath,
                        (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                        (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                        null,
                        ref errors,
                        ref warnings
                    );

                    if (!success)
                    {
                        result.Success = false;
                        result.ErrorMessage = $"Save failed. Errors: {errors}, Warnings: {warnings}";
                        return result;
                    }

                    result.SavedFiles.Add(newPath);
                }

                // Calculate total size
                result.TotalSize = 0;
                foreach (var file in result.SavedFiles)
                {
                    if (File.Exists(file))
                    {
                        result.TotalSize += new FileInfo(file).Length;
                    }
                }

                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                return new SaveAssemblyResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        #endregion

        #region Helper Methods

        private void CloseDocument()
        {
            if (swModel != null)
            {
                swApp.CloseDoc(swModel.GetTitle());
                swModel = null;
                swAssembly = null;
            }
        }

        private int GetComponentCount()
        {
            if (swAssembly == null) return 0;

            object[] components = (object[])swAssembly.GetComponents(false);
            return components?.Length ?? 0;
        }

        private List<string> GetFeatureList()
        {
            var features = new List<string>();

            Feature feat = (Feature)swModel.FirstFeature();
            while (feat != null)
            {
                if (!feat.GetTypeName2().StartsWith("Reference"))
                {
                    features.Add($"{feat.Name} ({feat.GetTypeName2()})");
                }
                feat = (Feature)feat.GetNextFeature();
            }

            return features;
        }

        private List<SketchInfo> GetSketchList()
        {
            var sketches = new List<SketchInfo>();

            Feature feat = (Feature)swModel.FirstFeature();
            while (feat != null)
            {
                if (feat.GetTypeName2() == "ProfileFeature")
                {
                    var sketch = (Sketch)feat.GetSpecificFeature2();
                    if (sketch != null)
                    {
                        object[] segments = (object[])sketch.GetSketchSegments();
                        int segmentCount = segments?.Length ?? 0;

                        sketches.Add(new SketchInfo
                        {
                            Name = feat.Name,
                            SegmentCount = segmentCount,
                            IsActive = sketch.Is3D()
                        });
                    }
                }
                feat = (Feature)feat.GetNextFeature();
            }

            return sketches;
        }

        private DisplayDimension FindDimension(string featureName, string dimensionName)
        {
            Feature feat = null;

            // Find feature by name
            Feature tempFeat = (Feature)swModel.FirstFeature();
            while (tempFeat != null)
            {
                if (tempFeat.Name == featureName)
                {
                    feat = tempFeat;
                    break;
                }
                tempFeat = (Feature)tempFeat.GetNextFeature();
            }

            if (feat == null) return null;

            DisplayDimension dispDim = (DisplayDimension)feat.GetFirstDisplayDimension();
            while (dispDim != null)
            {
                Dimension dim = (Dimension)dispDim.GetDimension();
                if (dim.FullName == dimensionName || dim.Name == dimensionName)
                {
                    return dispDim;
                }
                dispDim = (DisplayDimension)feat.GetNextDisplayDimension(dispDim);
            }

            return null;
        }

        private Component2 FindComponent(string componentName)
        {
            object[] components = (object[])swAssembly.GetComponents(false);

            foreach (Component2 comp in components)
            {
                if (comp.Name2 == componentName ||
                    comp.GetSelectByIDString().Contains(componentName))
                {
                    return comp;
                }
            }

            return null;
        }

        private string GetDimensionUnits(Dimension dim)
        {
            // Get document units
            int lengthUnit = swModel.GetUserPreferenceIntegerValue(
                (int)swUserPreferenceIntegerValue_e.swUnitsLinear
            );

            switch (lengthUnit)
            {
                case (int)swLengthUnit_e.swMM: return "mm";
                case (int)swLengthUnit_e.swCM: return "cm";
                case (int)swLengthUnit_e.swMETER: return "m";
                case (int)swLengthUnit_e.swINCHES: return "in";
                case (int)swLengthUnit_e.swFEET: return "ft";
                default: return "unknown";
            }
        }

        private string ExtractThumbnail(string filePath)
        {
            // Thumbnail extraction requires either:
            // 1. Document Manager API (separate license)
            // 2. Taking a screenshot of the current view
            // 3. Using Windows Shell to extract embedded thumbnails

            // For now, return null - implement this based on your specific needs
            // The UI will work fine without thumbnails

            /* Example implementation options:
            
            // Option 1: Save current view as image (requires open document)
            if (swModel != null)
            {
                string tempPath = Path.Combine(Path.GetTempPath(), "temp_thumb.bmp");
                swModel.SaveBMP(tempPath, 200, 200);
                return tempPath;
            }
            
            // Option 2: Use Document Manager API
            // Requires separate license and SwDocumentMgr reference
            
            // Option 3: Extract Windows thumbnail
            // Use Shell32 or Windows API
            */

            return null;
        }

        private string FindMaterialDatabase(string materialName)
        {
            // Common SolidWorks material database paths
            string swPath = swApp.GetExecutablePath();
            string swDir = System.IO.Path.GetDirectoryName(swPath);

            // Try standard material database locations
            string[] possiblePaths = new string[]
            {
                Path.Combine(swDir, @"lang\english\sldmaterials\SolidWorks Materials.sldmat"),
                Path.Combine(swDir, @"lang\english\sldmaterials\Custom Materials.sldmat"),
                Path.Combine(swDir, @"sldmaterials\SolidWorks Materials.sldmat")
            };

            // Check which database file exists
            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    // In production, you'd check if the material exists in this database
                    return path;
                }
            }

            // Return empty if not found
            return "";
        }

        private void SelectEdge(string edgeName)
        {
            // This is a simplified implementation
            // In a real implementation, you would:
            // 1. Parse the edge reference (e.g., "Edge<1>")
            // 2. Find the actual edge in the model
            // 3. Select it using SelectByID2

            try
            {
                // Example: Select edge by name
                // The actual edge name format depends on your model
                swModel.Extension.SelectByID2(
                    edgeName,           // Name
                    "EDGE",            // Type
                    0, 0, 0,           // X, Y, Z coordinates (not used for edges)
                    true,              // Append to selection
                    0,                 // Mark
                    null,              // Callout
                    0                  // Selection option
                );
            }
            catch
            {
                // Log error in production
            }
        }

        private List<Modification> ParseModificationJson(string json)
        {
            // Simple JSON parsing implementation
            // In production, use Newtonsoft.Json or System.Text.Json
            var modifications = new List<Modification>();

            // Mock implementation - replace with actual JSON parsing
            try
            {
                // Example of what the parsed data might look like
                modifications.Add(new Modification
                {
                    Type = "dimension",
                    FeatureName = "Sketch1",
                    DimensionName = "D1@Sketch1",
                    CurrentValue = 10.0,
                    NewValue = 12.0,
                    Units = "mm"
                });
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to parse JSON: {ex.Message}");
            }

            return modifications;
        }

        #endregion
    }

    #region Result Classes

    public class AssemblyInfo
    {
        public string Name { get; set; }
        public string FilePath { get; set; }
        public long FileSize { get; set; }
        public int PartCount { get; set; }
        public string ThumbnailPath { get; set; }
        public List<string> Features { get; set; }
        public List<SketchInfo> Sketches { get; set; }
    }

    public class SketchInfo
    {
        public string Name { get; set; }
        public int SegmentCount { get; set; }
        public bool IsActive { get; set; }
    }

    public class ModificationResult
    {
        public string FeatureName { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public string OldValue { get; set; }
        public string NewValue { get; set; }
    }

    public class DimensionModificationResult
    {
        public bool Success { get; set; }
        public double OldValue { get; set; }
        public double NewValue { get; set; }
        public string Units { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class MaterialChangeResult
    {
        public bool Success { get; set; }
        public string OldMaterial { get; set; }
        public string NewMaterial { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class FeatureAddResult
    {
        public bool Success { get; set; }
        public string FeatureName { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class SaveAssemblyResult
    {
        public bool Success { get; set; }
        public List<string> SavedFiles { get; set; }
        public long TotalSize { get; set; }
        public string ErrorMessage { get; set; }
    }

    #endregion
}