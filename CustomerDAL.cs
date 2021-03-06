﻿using rototrack_data_access;
using rototrack_model;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using zochnet_utils;

namespace QBMigrationTool
{
    public class CustomerDAL
    {
        public static XmlDocument BuildQueryRequest(string activeStatus, string fromModifiedDate, string toModifiedDate, string ownerID)
        {
            XmlDocument doc = XmlUtils.MakeRequestDocument();
            XmlElement parent = XmlUtils.MakeRequestParentElement(doc);
            XmlElement queryElement = doc.CreateElement("CustomerQueryRq");
            parent.AppendChild(queryElement);

            queryElement.AppendChild(XmlUtils.MakeSimpleElem(doc, "ActiveStatus", activeStatus));
            queryElement.AppendChild(XmlUtils.MakeSimpleElem(doc, "FromModifiedDate", fromModifiedDate));
            queryElement.AppendChild(XmlUtils.MakeSimpleElem(doc, "ToModifiedDate", toModifiedDate));            
            queryElement.AppendChild(XmlUtils.MakeSimpleElem(doc, "OwnerID", ownerID));

            return doc;
        }               

        public static void HandleResponse(string response)
        {
           WalkCustomerQueryRs(response);
        }

        private static void WalkCustomerQueryRs(string response)
        {
            //Parse the response XML string into an XmlDocument
            XmlDocument responseXmlDoc = new XmlDocument();
            responseXmlDoc.LoadXml(response);

            //Get the response for our request
            XmlNodeList CustomerQueryRsList = responseXmlDoc.GetElementsByTagName("CustomerQueryRs");
            if (CustomerQueryRsList.Count == 1) //Should always be true since we only did one request in this sample
            {
                XmlNode responseNode = CustomerQueryRsList.Item(0);
                //Check the status code, info, and severity
                XmlAttributeCollection rsAttributes = responseNode.Attributes;
                string statusCode = rsAttributes.GetNamedItem("statusCode").Value;
                string statusSeverity = rsAttributes.GetNamedItem("statusSeverity").Value;
                string statusMessage = rsAttributes.GetNamedItem("statusMessage").Value;                

                //status code = 0 all OK, > 0 is warning
                if (Convert.ToInt32(statusCode) >= 0)
                {
                    XmlNodeList CustomerRetList = responseNode.SelectNodes("//CustomerRet");//XPath Query
                    for (int i = 0; i < CustomerRetList.Count; i++)
                    {
                        XmlNode CustomerRet = CustomerRetList.Item(i);                       
                        WalkCustomerRet(CustomerRet);                       
                    }
                }
            }
        }                

        private static void WalkCustomerRet(XmlNode CustomerRet)
        {
            if (CustomerRet == null) return;

            RotoTrackDb db = new RotoTrackDb();

            //Get value of Sublevel.  If it is not set to 0 or 1, then return (we only care about parents with sublevel of 0, and their direct descendents, sublevel of 1)
            string Sublevel = CustomerRet.SelectSingleNode("./Sublevel").InnerText;
            if (Sublevel != "0" && Sublevel != "1")
            {
                return;
            }

            // Archive all parent and child customers in a JobArchive table.  We use this for time and mileage and for debugging.
            string JobSublevel = Sublevel;
            string JobFullName = CustomerRet.SelectSingleNode("./FullName").InnerText;
            string JobName = CustomerRet.SelectSingleNode("./Name").InnerText;
            string JobListID = CustomerRet.SelectSingleNode("./ListID").InnerText;
            string JobEditSequence = CustomerRet.SelectSingleNode("./EditSequence").InnerText;
            string JobIsActive = CustomerRet.SelectSingleNode("./IsActive").InnerText;

            JobArchive ja = null;
            if (db.JobArchives.Any(j => j.QBListId == JobListID))
            {
                ja = db.JobArchives.First(j => j.QBListId == JobListID);
            }
            else
            {
                ja = new JobArchive();
                db.JobArchives.Add(ja);
            }

            ja.QBListId = JobListID;
            ja.QBEditSequence = JobEditSequence;
            ja.IsActive = (JobIsActive == "true") ? true : false;
            ja.Name = JobName;
            ja.FullName = JobFullName;
            ja.Sublevel = JobSublevel;

            db.SaveChanges();

            // Note that those that have a parent are actually jobs
            bool IsJob = false;
            string parentListID = "";
            XmlNode ParentRef = CustomerRet.SelectSingleNode("./ParentRef");
            if (ParentRef != null)
            {
                IsJob = true;
                parentListID = ParentRef.SelectSingleNode("./ListID").InnerText;
            }

            // If this is a Customer record (not a job record), add/update Customer information, first.
            Customer c = null;
            if (!IsJob)
            {
                string ListID = CustomerRet.SelectSingleNode("./ListID").InnerText;
                if (db.Customers.Any(f => f.QBListId == ListID))
                {
                    c = db.Customers.First(f => f.QBListId == ListID);
                }
                else
                {
                    c = new Customer();
                    db.Customers.Add(c);
                }
                c.QBListId = ListID;

                //Get value of Name
                string Name = CustomerRet.SelectSingleNode("./Name").InnerText;
                c.Name = Name;

                //Get value of IsActive
                string IsActive = "false";
                if (CustomerRet.SelectSingleNode("./IsActive") != null)
                {
                    IsActive = CustomerRet.SelectSingleNode("./IsActive").InnerText;
                }
                c.IsActive = (IsActive == "true") ? true : false;
            }
            // Else, update IsActive for the associated WorkOrder and then get the parent customer record so we can update info for the parent
            else
            {
                // Get associated work order and update the IsActive flag in case someone marked it inactive in QuickBooks.
                // Also, update some additional info that the user may set in QuickBooks that we need to sync back over.
                string ListID = CustomerRet.SelectSingleNode("./ListID").InnerText;
                if (db.WorkOrders.Any(f => f.QBListId == ListID))
                {
                    WorkOrder wo = db.WorkOrders.First(f => f.QBListId == ListID);

                    string IsActive = "false";
                    if (CustomerRet.SelectSingleNode("./IsActive") != null)
                    {
                        IsActive = CustomerRet.SelectSingleNode("./IsActive").InnerText;
                        if (IsActive == "false")
                        {
                            wo.Status = WorkOrderStatus.Inactive;
                        }
                    }
                    // Leave wo.Status alone if not marked Inactive.

                    //<DataExtName>Invoice Delivery Status</DataExtName><DataExtType>STR255TYPE</DataExtType><DataExtValue>Warranty</DataExtValue></DataExtRet><DataExtRet><OwnerID>0</OwnerID><DataExtName>Invoice Delivery Status Date</DataExtName><DataExtType>STR255TYPE</DataExtType><DataExtValue>09/12/2014</DataExtValue></DataExtRet>
                    bool gotInvoiceDeliveryStatus = false;
                    bool gotInvoiceDeliveryStatusDate = false;
                    XmlNodeList DataExtRetList = CustomerRet.SelectNodes("./DataExtRet");
                    if (DataExtRetList != null)
                    {
                        for (int i = 0; i < DataExtRetList.Count; i++)
                        {
                            XmlNode DataExtRet = DataExtRetList.Item(i);                          
                            //Get value of DataExtName
                            string DataExtName = DataExtRet.SelectSingleNode("./DataExtName").InnerText;
                            //Get value of DataExtType
                            string DataExtType = DataExtRet.SelectSingleNode("./DataExtType").InnerText;
                            //Get value of DataExtValue
                            string DataExtValue = DataExtRet.SelectSingleNode("./DataExtValue").InnerText;

                            
                            if (DataExtName == "Invoice Delivery Status")
                            {
                                //wo.InvoiceDeliveryStatus
                                switch (DataExtValue)
                                {
                                    case "Closed at Zero $":
                                        wo.InvoiceDeliveryStatus = InvoiceDeliveryStatus.ClosedAtZeroDollars;
                                        break;
    
                                    case "Warranty":
                                        wo.InvoiceDeliveryStatus = InvoiceDeliveryStatus.Warranty;
                                        break;

                                    case "Invoice Hold-Coding/Signature":
                                        wo.InvoiceDeliveryStatus = InvoiceDeliveryStatus.InvoiceHoldCodingSignature;
                                        break;

                                    case "Invoice Mailed":
                                        wo.InvoiceDeliveryStatus = InvoiceDeliveryStatus.InvoiceMailed;
                                        break;

                                    case "Invoice Emailed":
                                        wo.InvoiceDeliveryStatus = InvoiceDeliveryStatus.InvoiceEmailed;
                                        break;

                                    case "Invoice Thru ADP":
                                        wo.InvoiceDeliveryStatus = InvoiceDeliveryStatus.InvoiceThruADP;
                                        break;

                                    case "Invoice Hand Delivered":
                                        wo.InvoiceDeliveryStatus = InvoiceDeliveryStatus.InvoiceHandDelivered;
                                        break;

                                    case "Filed":
                                        wo.InvoiceDeliveryStatus = InvoiceDeliveryStatus.Filed;
                                        break;
                                                                                                                            
                                    default:
                                        Logging.RototrackErrorLog("QBMigrationTool: " + RototrackConfig.GetBuildType() + ": " + "Unknown Invoice Delivery Status:" + DataExtValue);
                                        break;
                                }

                                gotInvoiceDeliveryStatus = true;
                            }

                            else if (DataExtName == "Invoice Delivery Status Date")
                            {
                                if (DataExtValue.Length > 0)
                                {
                                    DateTime invoiceDeliveryStatusDate;
                                    if (DateTime.TryParse(DataExtValue, out invoiceDeliveryStatusDate))
                                    {
                                        wo.InvoiceDeliveryStatusDate = invoiceDeliveryStatusDate;
                                    }
                                    gotInvoiceDeliveryStatusDate = true;
                                }
                            }
                                                          
                            else if (DataExtName == "Reason for Lost Quote")
                            {                              
                                switch (DataExtValue)
                                {
                                    case "Price Level":
                                        wo.ReasonForLostQuote = ReasonForLostQuote.PriceLevel;
                                        break;

                                    case "Delivery of Service":
                                        wo.ReasonForLostQuote = ReasonForLostQuote.DeliveryOfService;
                                        break;

                                        case "Slow Quote Response":
                                        wo.ReasonForLostQuote = ReasonForLostQuote.SlowQuoteResponse;
                                        break;

                                        case "Relationship w Competitor":
                                        wo.ReasonForLostQuote = ReasonForLostQuote.RelationshipWithCompetitor;
                                        break;

                                        case "Billing/ Invoicing":
                                        wo.ReasonForLostQuote = ReasonForLostQuote.BillingInvoicing;
                                        break;

                                        case "Warranty":
                                        wo.ReasonForLostQuote = ReasonForLostQuote.Warranty;
                                        break;

                                        case "Lack of Knowledge":
                                        wo.ReasonForLostQuote = ReasonForLostQuote.LackOfKnowledge;
                                        break;

                                        case "Lack of Product Choice":
                                        wo.ReasonForLostQuote = ReasonForLostQuote.LackOfProductChoice;
                                        break;

                                        case "In-House (Customer)":
                                        wo.ReasonForLostQuote = ReasonForLostQuote.InHouseCustomer;
                                        break;

                                        case "None":
                                        wo.ReasonForLostQuote = ReasonForLostQuote.None;
                                        break;

                                        case "Budgetary Estimate":
                                        wo.ReasonForLostQuote = ReasonForLostQuote.BudgetaryEstimate;
                                        break;
                                        

                                    default:
                                        Logging.RototrackErrorLog("QBMigrationTool: " + RototrackConfig.GetBuildType() + ": " + "Unknown Reason for Lost Quote");
                                        break;
                                }                                                             
                            }
                                                           
                            else if (DataExtName == "Collection Status")
                            {                                
                                switch (DataExtValue)
                                {
                                    case "Dispute - Labor Rate":
                                        wo.CollectionStatus = CollectionStatus.DisputeLaborRate;
                                        break;

                                    case "Dispute - Parts Rate":
                                        wo.CollectionStatus = CollectionStatus.DisputePartsRate;
                                        break;

                                    case "Missing - Codes":
                                        wo.CollectionStatus = CollectionStatus.MissingCodes;
                                        break;

                                    case "Missing - PO/ AFE #":
                                        wo.CollectionStatus = CollectionStatus.MissingPOAFE;
                                        break;

                                    case "Missing - Signature":
                                        wo.CollectionStatus = CollectionStatus.MissingSignature;
                                        break;

                                    case "Request - Approver Name":
                                        wo.CollectionStatus = CollectionStatus.RequestApproverName;
                                        break;

                                    case "Request - DSR Copies":
                                        wo.CollectionStatus = CollectionStatus.RequestDSRCopies;
                                        break;

                                    case "Request - Parts Receipts":
                                        wo.CollectionStatus = CollectionStatus.RequestPartsReceipts;
                                        break;

                                    case "Request - Resubmit":
                                        wo.CollectionStatus = CollectionStatus.RequestResubmit;
                                        break;

                                    case "Request - Revision per Quote":
                                        wo.CollectionStatus = CollectionStatus.RequestRevisionPerQuote;
                                        break;

                                    case "In Process":
                                        wo.CollectionStatus = CollectionStatus.InProcess;
                                        break;
                                                                                
                                    case "In Process - Customer Queue":
                                        wo.CollectionStatus = CollectionStatus.InProcessCustomerQueue;
                                        break;

                                    case "In Process-Cust  Approve Paym":
                                        wo.CollectionStatus = CollectionStatus.InProcessCustomerApprovePayment;
                                        break;

                                    case "Paid":
                                        wo.CollectionStatus = CollectionStatus.Paid;
                                        break;

                                    case "Write-off":
                                        wo.CollectionStatus = CollectionStatus.WriteOff;
                                        break;

                                    default:
                                        Logging.RototrackErrorLog("QBMigrationTool: " + RototrackConfig.GetBuildType() + ": " + "Unknown Collection Status");
                                        break;
                                }
                            }

                        }
                    }

                    if (gotInvoiceDeliveryStatus && gotInvoiceDeliveryStatusDate)
                    {
                        // Don't update if we set it to Inactive above or if it was already set to Inactive
                        if (wo.Status != WorkOrderStatus.Inactive)
                        {
                            wo.Status = WorkOrderStatus.Invoiced;
                        }
                    }

                    db.SaveChanges();                    
                }

                // Get parent customer
                c = db.Customers.First(f => f.QBListId == parentListID);
            }

            string EditSequence = CustomerRet.SelectSingleNode("./EditSequence").InnerText;
            c.QBEditSequence = EditSequence;

            if (CustomerRet.SelectSingleNode("./CompanyName") != null)
            {
                string CompanyName = CustomerRet.SelectSingleNode("./CompanyName").InnerText;
                c.CompanyName = CompanyName;
            }

            XmlNode BillAddress = CustomerRet.SelectSingleNode("./BillAddress");
            if (BillAddress != null)
            {
                //Get value of Addr1
                if (CustomerRet.SelectSingleNode("./BillAddress/Addr1") != null)
                {
                    string Addr1 = CustomerRet.SelectSingleNode("./BillAddress/Addr1").InnerText;
                    c.Address.Address1 = Addr1;
                }
                //Get value of Addr2
                if (CustomerRet.SelectSingleNode("./BillAddress/Addr2") != null)
                {
                    string Addr2 = CustomerRet.SelectSingleNode("./BillAddress/Addr2").InnerText;
                    c.Address.Address2 = Addr2;
                }
                //Get value of Addr3
                if (CustomerRet.SelectSingleNode("./BillAddress/Addr3") != null)
                {
                    string Addr3 = CustomerRet.SelectSingleNode("./BillAddress/Addr3").InnerText;
                    c.Address.Address3 = Addr3;
                }
                //Get value of Addr4
                if (CustomerRet.SelectSingleNode("./BillAddress/Addr4") != null)
                {
                    string Addr4 = CustomerRet.SelectSingleNode("./BillAddress/Addr4").InnerText;
                    c.Address.Address4 = Addr4;
                }
                //Get value of Addr5
                if (CustomerRet.SelectSingleNode("./BillAddress/Addr5") != null)
                {
                    string Addr5 = CustomerRet.SelectSingleNode("./BillAddress/Addr5").InnerText;
                    c.Address.Address5 = Addr5;
                }
                //Get value of City
                if (CustomerRet.SelectSingleNode("./BillAddress/City") != null)
                {
                    string City = CustomerRet.SelectSingleNode("./BillAddress/City").InnerText;
                    c.Address.City = City;
                }
                //Get value of State
                if (CustomerRet.SelectSingleNode("./BillAddress/State") != null)
                {
                    string State = CustomerRet.SelectSingleNode("./BillAddress/State").InnerText;
                    c.Address.State = State;
                }
                //Get value of PostalCode
                if (CustomerRet.SelectSingleNode("./BillAddress/PostalCode") != null)
                {
                    string PostalCode = CustomerRet.SelectSingleNode("./BillAddress/PostalCode").InnerText;
                    c.Address.Zip = PostalCode;
                }

            }
            //Done with field values for BillAddress aggregate

            // Add any new contacts (that were added to QB, or existed initially)
            AddNewContactsFromQB(c, CustomerRet);

            // Save changes to the database
            if (c != null)
            {
                db.SaveChanges();
            }
        }

        private static void AddNewContactsFromQB(Customer c, XmlNode CustomerRet)
        {
            Contact mainContact = null;
            if (CustomerRet.SelectSingleNode("./Contact") != null)
            {
                string MainContact = CustomerRet.SelectSingleNode("./Contact").InnerText;
                if (!c.Contacts.Any(f => f.Name == MainContact))
                {
                    mainContact = new Contact();
                    mainContact.Name = MainContact;
                    mainContact.IsActive = true;
                    c.Contacts.Add(mainContact);
                }
            }

            Contact altContact = null;
            if (CustomerRet.SelectSingleNode("./AltContact") != null)
            {
                string AltContact = CustomerRet.SelectSingleNode("./AltContact").InnerText;
                if (!c.Contacts.Any(f => f.Name == AltContact))
                {
                    altContact = new Contact();
                    altContact.Name = AltContact;
                    altContact.IsActive = true;
                    c.Contacts.Add(altContact);
                }
            }

            //Get all field values for AdditionalContactRef aggregate
            XmlNodeList AdditionalContactRefs = CustomerRet.SelectNodes("./AdditionalContactRef");
            if (AdditionalContactRefs != null)
            {
                for (int i = 0; i < AdditionalContactRefs.Count; i++)
                {
                    XmlNode AdditionalContactRet = AdditionalContactRefs.Item(i);

                    //Get value of ContactName
                    string ContactName = AdditionalContactRet.SelectSingleNode("./ContactName").InnerText;
                    //Get value of ContactValue
                    string ContactValue = AdditionalContactRet.SelectSingleNode("./ContactValue").InnerText;

                    switch (ContactName)
                    {
                        case "Main Phone":
                            if (mainContact != null) mainContact.Phone = ContactValue;
                            break;
                        case "Main Email":
                            if (mainContact != null) mainContact.Email = ContactValue;
                            break;
                        case "Alt. Phone":
                            if (altContact != null) altContact.Phone = ContactValue;
                            break;
                        case "Alt. Email 1":
                            if (altContact != null) altContact.Email = ContactValue;
                            break;
                        default:
                            break;
                    }
                }
            }
        }

        public static XmlDocument BuildCustomerQueryByWorkorderNum(string woNum)
        {
            XmlDocument doc = new XmlDocument();
            doc.AppendChild(doc.CreateXmlDeclaration("1.0", null, null));
            doc.AppendChild(doc.CreateProcessingInstruction("qbxml", "version=\"12.0\""));
            XmlElement qbXML = doc.CreateElement("QBXML");
            doc.AppendChild(qbXML);
            XmlElement qbXMLMsgsRq = doc.CreateElement("QBXMLMsgsRq");
            qbXML.AppendChild(qbXMLMsgsRq);
            qbXMLMsgsRq.SetAttribute("onError", "stopOnError");
            XmlElement custAddRq = doc.CreateElement("CustomerQueryRq");
            qbXMLMsgsRq.AppendChild(custAddRq);            

            XmlElement NameFilter = doc.CreateElement("NameFilter");
            custAddRq.AppendChild(NameFilter);
            NameFilter.AppendChild(XmlUtils.MakeSimpleElem(doc, "MatchCriterion", "Contains"));
            NameFilter.AppendChild(XmlUtils.MakeSimpleElem(doc, "Name", woNum));
            custAddRq.AppendChild(XmlUtils.MakeSimpleElem(doc, "OwnerID", "0"));

            return doc;
        } 

        public static string GetQBIDFromResponse(string response)
        {
            string qbid = "";

            //Parse the response XML string into an XmlDocument
            XmlDocument responseXmlDoc = new XmlDocument();
            responseXmlDoc.LoadXml(response);

            //Get the response for our request
            XmlNodeList CustomerQueryRsList = responseXmlDoc.GetElementsByTagName("CustomerQueryRs");
            if (CustomerQueryRsList.Count == 1) //Should always be true since we only did one request in this sample
            {
                XmlNode responseNode = CustomerQueryRsList.Item(0);
                //Check the status code, info, and severity
                XmlAttributeCollection rsAttributes = responseNode.Attributes;
                string statusCode = rsAttributes.GetNamedItem("statusCode").Value;
                string statusSeverity = rsAttributes.GetNamedItem("statusSeverity").Value;
                string statusMessage = rsAttributes.GetNamedItem("statusMessage").Value;

                //status code = 0 all OK, > 0 is warning
                if (Convert.ToInt32(statusCode) >= 0)
                {
                    XmlNodeList CustomerRetList = responseNode.SelectNodes("//CustomerRet");//XPath Query
                    if (CustomerRetList.Count == 1)
                    {
                        XmlNode CustomerRet = CustomerRetList.Item(0);
                        qbid = GetQBIDFromCustomerRet(CustomerRet);
                    }
                }
            }

            return qbid;
        }

        public static string GetQBEditSequenceFromResponse(string response)
        {
            string qbes = "";

            //Parse the response XML string into an XmlDocument
            XmlDocument responseXmlDoc = new XmlDocument();
            responseXmlDoc.LoadXml(response);

            //Get the response for our request
            XmlNodeList CustomerQueryRsList = responseXmlDoc.GetElementsByTagName("CustomerQueryRs");
            if (CustomerQueryRsList.Count == 1) //Should always be true since we only did one request in this sample
            {
                XmlNode responseNode = CustomerQueryRsList.Item(0);
                //Check the status code, info, and severity
                XmlAttributeCollection rsAttributes = responseNode.Attributes;
                string statusCode = rsAttributes.GetNamedItem("statusCode").Value;
                string statusSeverity = rsAttributes.GetNamedItem("statusSeverity").Value;
                string statusMessage = rsAttributes.GetNamedItem("statusMessage").Value;

                //status code = 0 all OK, > 0 is warning
                if (Convert.ToInt32(statusCode) >= 0)
                {
                    XmlNodeList CustomerRetList = responseNode.SelectNodes("//CustomerRet");//XPath Query
                    if (CustomerRetList.Count == 1)
                    {
                        XmlNode CustomerRet = CustomerRetList.Item(0);
                        qbes = GetQBEditSequenceFromCustomerRet(CustomerRet);
                    }
                }
            }

            return qbes;
        }

        private static string GetQBIDFromCustomerRet(XmlNode CustomerRet)
        {
            string ListID = CustomerRet.SelectSingleNode("./ListID").InnerText;
            return ListID;
        }

        private static string GetQBEditSequenceFromCustomerRet(XmlNode CustomerRet)
        {
            string ListID = CustomerRet.SelectSingleNode("./EditSequence").InnerText;
            return ListID;
        }

        public static void UpdateAlreadyExistingWorkOrder(int woId)
        {
            RotoTrackDb db = new RotoTrackDb();
            WorkOrder wo = db.WorkOrders.Find(woId);

            if (wo != null)
            {
                string response = QBUtils.DoRequest(CustomerDAL.BuildCustomerQueryByWorkorderNum(wo.WorkOrderNumber));
                string qbid = CustomerDAL.GetQBIDFromResponse(response);
                string qbes = CustomerDAL.GetQBEditSequenceFromResponse(response);
                if ((qbid != "") && (qbes != ""))
                {
                    wo.QBListId = qbid;
                    wo.QBEditSequence = qbes;
                    db.Entry(wo).State = EntityState.Modified;
                    db.SaveChanges();
                }
            }
        }

    }
}
