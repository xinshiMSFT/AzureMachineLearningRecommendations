using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml;

namespace RecommendationsV2
{
    public class Recommendations
    {
        private static void Main(string[] args)
        {
            Console.WriteLine("Welcome to Azure Machine Learning Recommendations V2!");
            Console.Write("\nThis application is not intended to include full functionality, nor does it use all the APIs. ");
            Console.WriteLine("It demonstrates some common operations to perform when you first want to play with the Machine Learning recommendation service.");

            var email = ConfigurationManager.AppSettings["email"];
            var accountKey = ConfigurationManager.AppSettings["accountKey"];

            //
            // Initialization
            var recommendations = new Recommendations();
            Console.WriteLine("\nInitializing...");
            recommendations.Init(email, accountKey);

            //
            // Create a model container
            Console.WriteLine("\nCreating model container {0}...", modelName);
            var modelId = recommendations.CreateModel(modelName);

            //
            // Import data to the container
            Console.WriteLine("\nImporting catalog and usage data...");
            var resourcesDir = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Resources");
            recommendations.ImportFile(modelId, Path.Combine(resourcesDir, "catalog.txt"), Uris.ImportCatalog);
            recommendations.ImportFile(modelId, Path.Combine(resourcesDir, "usage.txt"), Uris.ImportUsage);

            //
            // Trigger a build to produce a recommendation model
            Console.WriteLine("\nTrigger build for model '{0}'", modelId);
            var buildId = recommendations.BuildModel(modelId, "build of " + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));

            //
            // Monitor the current triggered build
            Console.WriteLine("\nMonitoring build '{0}'", buildId);
            var status = BuildStatus.Create;
            bool monitor = true;
            while (monitor)
            {
                status = recommendations.GetBuidStatus(modelId, buildId);

                Console.Write("\tstatus: {0}", status);
                if (status != BuildStatus.Error && status != BuildStatus.Cancelled && status != BuildStatus.Success)
                {
                    Console.WriteLine(" --> will check again in 30 secs...");
                    Thread.Sleep(30000);
                }
                else
                {
                    monitor = false;
                }
            }

            Console.WriteLine("\n\tBuild {0} ended with status {1}", buildId, status);

            Console.WriteLine("\nWaiting for propagation of the built model...");
            Thread.Sleep(20000);

            //
            // Get recommendations
            var seedItems = new List<CatalogItem>
                {
                new CatalogItem() {Id = "1", Name = "Halogen Headlights"},
                    new CatalogItem() {Id = "8", Name = "Wheel Tire Combo"}
                };

            Console.WriteLine("\nGetting recommendation for a single item...");

            // Show usage for single item
            recommendations.InvokeRecommendations(modelId, seedItems, false);

            Console.WriteLine("\n\n\t Getting recommendation for a set of items...");

            // Show usage for a list of items
            recommendations.InvokeRecommendations(modelId, seedItems, true);

            //
            // Delete a model container
            Console.WriteLine("\nDeleting model container '{0}'...", modelName);
            recommendations.DeleteModel(modelId);

            Console.WriteLine("\nPress any key to end the demo...");
            Console.ReadKey();
        }

        public void Init(string username, string accountKey)
        {
            _httpClient = new HttpClient();
            var pass = GeneratePass(username, accountKey);
            Console.WriteLine("\taccess key generated");
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", pass);
            _httpClient.BaseAddress = new Uri(RootUri);
        }

        private string GeneratePass(string email, string accountKey)
        {
            var byteArray = Encoding.ASCII.GetBytes(string.Format("{0}:{1}", email, accountKey));
            return Convert.ToBase64String(byteArray);
        }

        public string CreateModel(string modelName)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, String.Format(Uris.CreateModelUrl, modelName));
            var response = _httpClient.SendAsync(request).Result;

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception(String.Format("Error {0}: Failed to create model {1}, \n reason {2}",
                    response.StatusCode, modelName, ExtractErrorInfo(response)));
            }

            string modelId = null;

            var node = XmlUtils.ExtractXmlElement(response.Content.ReadAsStreamAsync().Result, "//a:entry/a:content/m:properties/d:Id");
            if (node != null)
                modelId = node.InnerText;

            Console.WriteLine("\tModel '{0}' created with ID: {1}", modelName, modelId);

            return modelId;
        }

        private ImportReport ImportFile(string modelId, string filePath, string importUri)
        {
            var filestream = new FileStream(filePath, FileMode.Open);
            var fileName = Path.GetFileName(filePath);
            var request = new HttpRequestMessage(HttpMethod.Post, String.Format(importUri, modelId, fileName));

            request.Content = new StreamContent(filestream);
            var response = _httpClient.SendAsync(request).Result;

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception(
                    String.Format("Error {0}: Failed to import file {1}, for model {2} \n reason {3}",
                        response.StatusCode, filePath, modelId, ExtractErrorInfo(response)));
            }

            //process response if success
            var nodeList = XmlUtils.ExtractXmlElementList(response.Content.ReadAsStreamAsync().Result,
                "//a:entry/a:content/m:properties/*");

            var report = new ImportReport { Info = fileName };
            foreach (XmlNode node in nodeList)
            {
                if ("LineCount".Equals(node.LocalName))
                {
                    report.LineCount = int.Parse(node.InnerText);
                }
                if ("ErrorCount".Equals(node.LocalName))
                {
                    report.ErrorCount = int.Parse(node.InnerText);
                }
            }

            Console.WriteLine("\t{0}", report);

            return report;
        }

        public string BuildModel(string modelId, string buildDescription)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, String.Format(Uris.BuildModel, modelId, buildDescription));

            //setup the build parameters here we use a simple build without feature usage, for a complete list and
            //explanation check the API document AT
            //http://azure.microsoft.com/en-us/documentation/articles/machine-learning-recommendation-api-documentation/#1113-recommendation-build-parameters
            request.Content = new StringContent("<BuildParametersList>" +
                                                "<NumberOfModelIterations>10</NumberOfModelIterations>" +
                                                "<NumberOfModelDimensions>20</NumberOfModelDimensions>" +
                                                "<ItemCutOffLowerBound>1</ItemCutOffLowerBound>" +
                                                "<EnableModelingInsights>false</EnableModelingInsights>" +
                                                "<UseFeaturesInModel>false</UseFeaturesInModel>" +
                                                "<ModelingFeatureList></ModelingFeatureList>" +
                                                "<AllowColdItemPlacement>true</AllowColdItemPlacement>" +
                                                "<EnableFeatureCorrelation>false</EnableFeatureCorrelation>" +
                                                "<ReasoningFeatureList></ReasoningFeatureList>" +
                                                "</BuildParametersList>", Encoding.UTF8, "Application/xml");
            var response = _httpClient.SendAsync(request).Result;

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception(String.Format("Error {0}: Failed to start build for model {1}, \n reason {2}",
                    response.StatusCode, modelId, ExtractErrorInfo(response)));
            }
            string buildId = null;
            //process response if success
            var node = XmlUtils.ExtractXmlElement(response.Content.ReadAsStreamAsync().Result, "//a:entry/a:content/m:properties/d:Id");
            if (node != null)
                buildId = node.InnerText;

            Console.WriteLine("\ttriggered build id '{0}'", buildId);

            return buildId;
        }

        public BuildStatus GetBuidStatus(string modelId, string buildId)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, String.Format(Uris.BuildStatuses, modelId, false));
            var response = _httpClient.SendAsync(request).Result;

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception(String.Format("Error {0}: Failed to retrieve build for status for model {1} and build id {2}, \n reason {3}",
                    response.StatusCode, modelId, buildId, ExtractErrorInfo(response)));
            }
            string buildStatusStr = null;
            var node = XmlUtils.ExtractXmlElement(response.Content.ReadAsStreamAsync().Result, string.Format("//a:entry/a:content/m:properties[d:BuildId='{0}']/d:Status", buildId));
            if (node != null)
                buildStatusStr = node.InnerText;

            BuildStatus buildStatus;
            if (!Enum.TryParse(buildStatusStr, true, out buildStatus))
            {
                throw new Exception(string.Format("Failed to parse build status for value {0} of build {1} for model {2}", buildStatusStr, buildId, modelId));
            }

            return buildStatus;
        }

        private void InvokeRecommendations(string modelId, List<CatalogItem> seedItems, bool useList)
        {
            if (useList)
            {
                var recoItems = GetRecommendation(modelId, seedItems.Select(i => i.Id).ToList(), 5);
                Console.WriteLine("\n\tRecommendations for [{0}]", string.Join("] + [", seedItems));
                foreach (var recommendedItem in recoItems)
                {
                    Console.WriteLine("\t  Id: {0}", recommendedItem.Id);
                    Console.WriteLine("\t  Name: {0}", recommendedItem.Name);
                    Console.WriteLine("\t  Rating: {0}", recommendedItem.Rating);
                    Console.WriteLine("\t  Reasoning: {0}", recommendedItem.Reasoning);
                    Console.WriteLine("\n");
                }
            }
            else
            {
                foreach (var item in seedItems)
                {
                    var recoItems = GetRecommendation(modelId, new List<string> { item.Id }, 5);
                    Console.WriteLine("\n\tRecommendation for '{0}'", item);
                    foreach (var recommendedItem in recoItems)
                    {
                        Console.WriteLine("\t  Id: {0}", recommendedItem.Id);
                        Console.WriteLine("\t  Name: {0}", recommendedItem.Name);
                        Console.WriteLine("\t  Rating: {0}", recommendedItem.Rating);
                        Console.WriteLine("\t  Reasoning: {0}", recommendedItem.Reasoning);
                        Console.WriteLine("\n");
                    }
                }
            }
        }

        public IEnumerable<RecommendedItem> GetRecommendation(string modelId, List<string> itemIdList, int numberOfResult,
            bool includeMetadata = false)
        {
            var request = new HttpRequestMessage(HttpMethod.Get,
                String.Format(Uris.GetRecommendation, modelId, string.Join(",", itemIdList), numberOfResult,
                    includeMetadata));
            var response = _httpClient.SendAsync(request).Result;

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception(
                    String.Format(
                        "Error {0}: Failed to retrieve recommendation for item list {1} and model {2}, \n reason {3}",
                        response.StatusCode, string.Join(",", itemIdList), modelId, ExtractErrorInfo(response)));
            }
            var recoList = new List<RecommendedItem>();

            var nodeList = XmlUtils.ExtractXmlElementList(response.Content.ReadAsStreamAsync().Result, "//a:entry/a:content/m:properties");

            foreach (var node in (nodeList))
            {
                var item = new RecommendedItem();
                //cycle through the recommended items
                foreach (var child in ((XmlElement)node).ChildNodes)
                {
                    //cycle through properties
                    var nodeName = ((XmlNode)child).LocalName;
                    switch (nodeName)
                    {
                        case "Id":
                            item.Id = ((XmlNode)child).InnerText;
                            break;

                        case "Name":
                            item.Name = ((XmlNode)child).InnerText;
                            break;

                        case "Rating":
                            item.Rating = ((XmlNode)child).InnerText;
                            break;

                        case "Reasoning":
                            item.Reasoning = ((XmlNode)child).InnerText;
                            break;
                    }
                }
                recoList.Add(item);
            }
            return recoList;
        }

        private void DeleteModel(string modelId)
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, String.Format(Uris.DeleteModelUrl, modelId));
            var response = _httpClient.SendAsync(request).Result;

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception(String.Format("Error {0}: Failed to delete model {1}, \n reason {2}",
                    response.StatusCode, modelId, ExtractErrorInfo(response)));
            }

            Console.WriteLine("\tModel '{0}' with ID: {1} deleted", modelName, modelId);
        }

        private static string ExtractErrorInfo(HttpResponseMessage response)
        {
            //DM send the error message in body so need to extract the info from there
            string detailedReason = null;
            if (response.Content != null)
            {
                detailedReason = response.Content.ReadAsStringAsync().Result;
            }
            var errorMsg = detailedReason == null ? response.ReasonPhrase : response.ReasonPhrase + "->" + detailedReason;
            return errorMsg;
        }

        private class XmlUtils
        {
            internal static XmlNode ExtractXmlElement(Stream xmlStream, string xPath)
            {
                var xmlDoc = new XmlDocument();
                xmlDoc.Load(xmlStream);
                //Create namespace manager
                var nsmgr = CreateNamespaceManager(xmlDoc);

                var node = xmlDoc.SelectSingleNode(xPath, nsmgr);
                return node;
            }

            private static XmlNamespaceManager CreateNamespaceManager(XmlDocument xmlDoc)
            {
                var nsmgr = new XmlNamespaceManager(xmlDoc.NameTable);
                nsmgr.AddNamespace("a", "http://www.w3.org/2005/Atom");
                nsmgr.AddNamespace("m", "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata");
                nsmgr.AddNamespace("d", "http://schemas.microsoft.com/ado/2007/08/dataservices");
                return nsmgr;
            }

            internal static XmlNodeList ExtractXmlElementList(Stream xmlStream, string xPath)
            {
                var xmlDoc = new XmlDocument();
                xmlDoc.Load(xmlStream);
                var nsmgr = CreateNamespaceManager(xmlDoc);
                var nodeList = xmlDoc.SelectNodes(xPath, nsmgr);
                return nodeList;
            }
        }

        public class ImportReport
        {
            public string Info { get; set; }

            public int ErrorCount { get; set; }

            public int LineCount { get; set; }

            public override string ToString()
            {
                return string.Format("successfully imported {0}/{1} lines for {2}", LineCount - ErrorCount, LineCount,
                    Info);
            }
        }

        public static class Uris
        {
            public const string CreateModelUrl = "CreateModel?modelName=%27{0}%27&apiVersion=%271.0%27";

            public const string ImportCatalog = "ImportCatalogFile?modelId=%27{0}%27&filename=%27{1}%27&apiVersion=%271.0%27";

            public const string ImportUsage = "ImportUsageFile?modelId=%27{0}%27&filename=%27{1}%27&apiVersion=%271.0%27";

            public const string BuildModel = "BuildModel?modelId=%27{0}%27&userDescription=%27{1}%27&apiVersion=%271.0%27";

            public const string BuildStatuses = "GetModelBuildsStatus?modelId=%27{0}%27&onlyLastBuild={1}&apiVersion=%271.0%27";

            public const string GetRecommendation = "ItemRecommend?modelId=%27{0}%27&itemIds=%27{1}%27&numberOfResults={2}&includeMetadata={3}&apiVersion=%271.0%27";

            public const string DeleteModelUrl = "DeleteModel?id=%27{0}%27&apiVersion=%271.0%27";
        }

        public enum BuildStatus
        {
            Create,
            Queued,
            Building,
            Success,
            Error,
            Cancelling,
            Cancelled
        }

        public class CatalogItem
        {
            public string Id { get; set; }

            public string Name { get; set; }

            public override string ToString()
            {
                return string.Format("Id: {0}, Name: {1}", Id, Name);
            }
        }

        public class RecommendedItem
        {
            public string Name { get; set; }

            public string Rating { get; set; }

            public string Reasoning { get; set; }

            public string Id { get; set; }

            public override string ToString()
            {
                return string.Format("Name: {0}, Id: {1}, Rating: {2}, Reasoning: {3}", Name, Id, Rating, Reasoning);
            }
        }

        private HttpClient _httpClient;
        private const string RootUri = "https://api.datamarket.azure.com/amla/recommendations/v2/";
        private const string modelName = "demomodel";
    }
}