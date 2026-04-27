using System.ServiceModel;

namespace QBBridge.Service.Soap;

/// <summary>
/// QBWC SOAP contract — implements the verbs the QuickBooks Web Connector expects.
/// Spec: https://developer.intuit.com/app/developer/qbdesktop/docs/develop/getting-started-with-quickbooks-web-connector
///
/// QBWC (running on J-DC2) acts as the intermediary between this bridge and QuickBooks.
/// Our bridge returns qbXML queries on sendRequestXML; QBWC feeds them to QB via COM and
/// hands the response back on receiveResponseXML. The bridge is stateless between runs.
/// </summary>
[ServiceContract(Namespace = "http://developer.intuit.com/")]
public interface IQbwcService
{
    /// <summary>
    /// QBWC sends credentials. Return an array: [ticket, company-file-path-or-empty].
    /// Empty company-file means "use whichever .qbw file QBWC currently has open".
    /// Returning ["","nvu"] means credentials invalid.
    /// </summary>
    [OperationContract(Action = "http://developer.intuit.com/authenticate")]
    string[] authenticate(string strUserName, string strPassword);

    /// <summary>
    /// Called repeatedly until we return an empty string. Each call we return one qbXML
    /// request. QBWC passes it to QB and hands us the response via receiveResponseXML.
    /// </summary>
    [OperationContract(Action = "http://developer.intuit.com/sendRequestXML")]
    string sendRequestXML(
        string ticket,
        string strHCPResponse,
        string strCompanyFileName,
        string qbXMLCountry,
        int qbXMLMajorVers,
        int qbXMLMinorVers);

    /// <summary>
    /// QBWC hands us QB's response. Return 0–100 (% complete) to continue, negative to abort.
    /// We parse the response, post to InTime backend, return percent done.
    /// </summary>
    [OperationContract(Action = "http://developer.intuit.com/receiveResponseXML")]
    int receiveResponseXML(string ticket, string response, string hresult, string message);

    /// <summary>
    /// Called when QBWC fails to connect to QB. Return string controls retry behavior:
    /// "done" = give up, "" = retry immediately, any other = show to user and stop.
    /// </summary>
    [OperationContract(Action = "http://developer.intuit.com/connectionError")]
    string connectionError(string ticket, string hresult, string message);

    /// <summary>
    /// Called if receiveResponseXML returned negative. Return a human-readable error message.
    /// </summary>
    [OperationContract(Action = "http://developer.intuit.com/getLastError")]
    string getLastError(string ticket);

    /// <summary>
    /// End of run. Return a human-readable summary ("OK: 47 invoices updated" etc).
    /// </summary>
    [OperationContract(Action = "http://developer.intuit.com/closeConnection")]
    string closeConnection(string ticket);

    /// <summary>
    /// Version probe. Return SDK version; we use "2.3" which QBWC accepts through QB 2024.
    /// </summary>
    [OperationContract(Action = "http://developer.intuit.com/serverVersion")]
    string serverVersion();

    /// <summary>
    /// Client version handshake. Return empty string to accept, "W:&lt;msg&gt;" to warn,
    /// "E:&lt;msg&gt;" to refuse. We always accept.
    /// </summary>
    [OperationContract(Action = "http://developer.intuit.com/clientVersion")]
    string clientVersion(string strVersion);
}
