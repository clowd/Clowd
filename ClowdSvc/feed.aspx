<%@ Page Language="C#" %>

<%@ Import Namespace="System" %>
<%@ Import Namespace="System.Collections.Generic" %>
<%@ Import Namespace="System.IO" %>
<%@ Import Namespace="System.Xml.Linq" %>

<script language="c#" runat="server">
public void Page_Load(object sender, EventArgs e)
{
    string req = "latest";
	char branch = 's';
		
	try 
	{
		var tmp = Request.QueryString["v"];
		if(!String.IsNullOrWhiteSpace(tmp))
		{
			req = tmp;
		}
	} 
	catch { }
		
	try 
	{
		var tmp = Request.QueryString["b"];
		if(!String.IsNullOrWhiteSpace(tmp) && tmp.Length == 1)
		{
			branch = tmp[0];
		}
	} 
	catch { }
		
        
    if (!req.Equals("latest", StringComparison.InvariantCultureIgnoreCase) && Char.IsLetter(req[req.Length - 1]))
    {
        branch = req[req.Length - 1];
        req = req.Substring(0, req.Length - 1);
    }
    string directory = Path.GetDirectoryName(Request.PhysicalPath);
    decimal version = default(decimal);
    if (!req.Equals("latest", StringComparison.InvariantCultureIgnoreCase))
    {
        decimal t;
        if (decimal.TryParse(req, out t))
        {
            if (Directory.Exists(Path.Combine(directory, t.ToString() + branch)))
            {
                version = t;
            }
        }
    }
    if (version == default(decimal))
    {
        foreach (string dir in Directory.GetDirectories(directory))
        {
            string name = Path.GetFileName(dir);
            char b = name[name.Length - 1];
            if (b == branch)
            {
                name = name.Substring(0, name.Length - 1);
                var v = decimal.Parse(name);
                if (v > version)
                    version = v;
            }
        }
    }

    var vdir = Path.Combine(directory, version.ToString() + branch);
    XElement feed = new XElement("Feed");
    feed.SetAttributeValue("BaseUrl", "http://clowd.ca/app_updates/" + version.ToString() + branch + "/");
    XElement tasks = new XElement("Tasks");
    foreach (var file in GetFiles(vdir))
    {
        var relative = GetRelativePath(file, vdir);
        var fileInfo = new FileInfo(file);
        XElement fut = new XElement("FileUpdateTask");
        fut.SetAttributeValue("localPath", relative);
        fut.SetAttributeValue("lastModified", fileInfo.LastWriteTime.ToFileTime().ToString(System.Globalization.CultureInfo.InvariantCulture));
        fut.SetAttributeValue("fileSize", fileInfo.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));

        XElement cond = new XElement("Conditions");

        XElement checksum = new XElement("FileChecksumCondition");
        checksum.SetAttributeValue("type", "not");
        checksum.SetAttributeValue("checksumType", "sha256");
        checksum.SetAttributeValue("checksum", GetSHA256Checksum(file));

        cond.Add(checksum);
        fut.Add(cond);
        tasks.Add(fut);
    }
    feed.Add(tasks);

    Response.ContentType = "text/xml";
    Response.Write("<?xml version=\"1.0\" encoding=\"utf-8\"?>" + Environment.NewLine);
    Response.Write(feed.ToString());
}
public string GetSHA256Checksum(string filePath)
{
    using (FileStream stream = File.OpenRead(filePath))
    {
        var sha = new System.Security.Cryptography.SHA256Managed();
        byte[] checksum = sha.ComputeHash(stream);
        return BitConverter.ToString(checksum).Replace("-", String.Empty);
    }
}
public string GetRelativePath(string filespec, string folder)
{
    Uri pathUri = new Uri(filespec);
    // Folders must end in a slash
    if (!folder.EndsWith(Path.DirectorySeparatorChar.ToString()))
    {
        folder += Path.DirectorySeparatorChar;
    }
    Uri folderUri = new Uri(folder);
    return Uri.UnescapeDataString(folderUri.MakeRelativeUri(pathUri).ToString().Replace('/', Path.DirectorySeparatorChar));
}
public IEnumerable<string> GetFiles(string path)
{
    Queue<string> queue = new Queue<string>();
    queue.Enqueue(path);
    while (queue.Count > 0)
    {
        path = queue.Dequeue();
        try
        {
            foreach (string subDir in Directory.GetDirectories(path))
            {
                queue.Enqueue(subDir);
            }
        }
        catch (Exception ex)
        {
            //Console.Error.WriteLine(ex);
        }
        string[] files = null;
        try
        {
            files = Directory.GetFiles(path);
        }
        catch (Exception ex)
        {
            //Console.Error.WriteLine(ex);
        }
        if (files != null)
        {
            for (int i = 0; i < files.Length; i++)
            {
                yield return files[i];
            }
        }
    }
}
</script>

