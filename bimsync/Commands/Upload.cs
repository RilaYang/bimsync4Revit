﻿#region Namespaces
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Diagnostics;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Autodesk.Revit.DB.ExtensibleStorage;
using RestSharp;
#endregion

namespace bimsync.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class Upload : IExternalCommand
    {
        private string _path;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Application app = uiapp.Application;
            Document doc = uidoc.Document;

            using (Transaction tx = new Transaction(doc))
            {
                try
                {
                    //Refresh the token and save it
                    Token token = Services.RefreshToken(Properties.Settings.Default["Token"] as Token);
                    Properties.Settings.Default["Token"] = token;
                    Properties.Settings.Default.Save();

                    string access_token = token.access_token;

                    //Load the interface to select these project and model
                    UI.ModelSelection modelSelection = new UI.ModelSelection(access_token, doc);

                    if (modelSelection.ShowDialog() == true)
                    {
                        tx.Start("Export to bimsync");

                        //If necessary, add Shared parameters to store bimsync model and project
                        AddSharedParameters(app, doc);

                        //Write the values to these parameters
                        WriteOnParam("project_id", doc, modelSelection.ProjectId);
                        WriteOnParam("model_id", doc, modelSelection.ModelId);

                        //Export IFC
                        ExportToIFC(doc);

                        UploadTobimsync(modelSelection,access_token);

                        tx.Commit();

                        // Return Success
                        return Result.Succeeded;
                    }
                    else
                    {
                        return Autodesk.Revit.UI.Result.Cancelled;
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException exceptionCanceled)
                {
                    message = exceptionCanceled.Message;
                    if (tx.HasStarted() == true)
                    {
                        tx.RollBack();
                    }
                    return Autodesk.Revit.UI.Result.Cancelled;
                }
                catch (Exception ex)
                {
                    // unchecked exception cause command failed
                    message = ex.Message;
                    //Trace.WriteLine(ex.ToString());
                    if (tx.HasStarted() == true)
                    {
                        tx.RollBack();
                    }
                    return Autodesk.Revit.UI.Result.Failed;
                }
            }
        }

        public void ExportToIFC(Document doc)
        {
            //Export the project as IFC
            IFCExportOptions IFCOptions = new IFCExportOptions();
            IFCOptions.ExportBaseQuantities = true;
            IFCOptions.FileVersion = IFCVersion.IFC2x3;
            IFCOptions.WallAndColumnSplitting = true;
            IFCOptions.SpaceBoundaryLevel = 1;

            string folder = System.IO.Path.GetTempPath();
            string name = DateTime.Now.ToString("yyyyMMddHHmmss") + "_" + System.IO.Path.GetFileNameWithoutExtension(doc.PathName) + ".ifc";

            _path = Path.Combine(folder, name);

            doc.Export(folder, name, IFCOptions);
        }

        public void UploadTobimsync(UI.ModelSelection modelSelection, string access_token)
        {
            RestClient client = new RestClient("https://api.bimsync.com");
            string filename = Path.GetFileName(_path);

            //Upload the IFC model
            RestRequest revisionRequest = new RestRequest("v2/projects/" + modelSelection.ProjectId + "/revisions", Method.POST);
            revisionRequest.AddHeader("Authorization", "Bearer " + access_token);
            revisionRequest.AddHeader("Content-Type", "application/ifc");
            revisionRequest.AddHeader("Bimsync-Params", "{" +
                "\"callbackUrl\": \"http://127.0.0.1:63842/\"," +
                "\"comment\": \"" + modelSelection.Comment + "\"," +
                "\"filename\": \"" + filename + "\"," +
                "\"model\": \"" + modelSelection.ModelId + "\"}");

            byte[] data = File.ReadAllBytes(_path);
            revisionRequest.AddParameter("application/ifc", data, RestSharp.ParameterType.RequestBody);

            var reponsetest = client.Execute(revisionRequest);
            //
        }

        private void AddSharedParameters(Application app, Document doc)
        {
            //Save the previous shared param file path
            string previousSharedParam = app.SharedParametersFilename;

            //Extract shared param to a txt file
            string tempPath = System.IO.Path.GetTempPath();
            string SPPath = Path.Combine(tempPath, "bimsyncSharedParameter.txt");

            if (!File.Exists(SPPath))
            {
                //extract the familly
                List<string> files = new List<string>();
                files.Add("bimsyncSharedParameter.txt");
                Services.ExtractEmbeddedResource(tempPath, "bimsync.Resources", files);
            }

            //set the shared param file
            app.SharedParametersFilename = SPPath;

            //Define a category set containing the project properties
            CategorySet myCategorySet = app.Create.NewCategorySet();
            Categories categories = doc.Settings.Categories;
            Category projectCategory = categories.get_Item(BuiltInCategory.OST_ProjectInformation);
            myCategorySet.Insert(projectCategory);

            //Retrive shared parameters
            DefinitionFile myDefinitionFile = app.OpenSharedParameterFile();

            DefinitionGroup definitionGroup = myDefinitionFile.Groups.get_Item("bimsync");

            foreach (Definition paramDef in definitionGroup.Definitions)
            {
                // Get the BingdingMap of current document.
                BindingMap bindingMap = doc.ParameterBindings;

                //the parameter does not exist
                if (!bindingMap.Contains(paramDef))
                {
                    //Create an instance of InstanceBinding
                    InstanceBinding instanceBinding = app.Create.NewInstanceBinding(myCategorySet);

                    bindingMap.Insert(paramDef, instanceBinding, BuiltInParameterGroup.PG_IDENTITY_DATA);
                }
                //the parameter is not added to the correct categories
                else if (bindingMap.Contains(paramDef))
                {
                    InstanceBinding currentBinding = bindingMap.get_Item(paramDef) as InstanceBinding;
                    currentBinding.Categories = myCategorySet;
                    bindingMap.ReInsert(paramDef, currentBinding, BuiltInParameterGroup.PG_IDENTITY_DATA);
                }
            }

            //Reset to the previous shared parameters text file
            app.SharedParametersFilename = previousSharedParam;
            File.Delete(SPPath);

        }

        private void WriteOnParam(string paramId, Document doc, string value)
        {
            ProjectInfo projectInfoElement = doc.ProjectInformation;

            IList<Autodesk.Revit.DB.Parameter> parameters = projectInfoElement.GetParameters(paramId);
            if (parameters.Count != 0)
            {
                Autodesk.Revit.DB.Parameter p = parameters.FirstOrDefault();
                if (!p.IsReadOnly)
                {
                    p.Set(value);
                }
            }
        }
    }
}