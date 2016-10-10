﻿namespace Microsoft.Content.Build.Code2Yaml.Steps
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using System.Xml.Linq;
    using System.Xml.XPath;

    using Microsoft.Content.Build.Code2Yaml.Common;
    using Microsoft.Content.Build.Code2Yaml.Constants;
    using Microsoft.Content.Build.Code2Yaml.DataContracts;
    using Microsoft.Content.Build.Code2Yaml.Utility;

    public class PreprocessXml : IStep
    {
        private static readonly Regex IdRegex = new Regex(@"^(namespace|class|struct|enum|interface)([\S\s]+)$", RegexOptions.Compiled);
        private static readonly Regex ToRegularizeTypeRegex = new Regex(@"^(public|protected|private)(?=.*?&lt;.*?&gt;)", RegexOptions.Compiled);

        public string StepName { get { return "Preprocess"; } }

        public async Task RunAsync(BuildContext context)
        {
            var config = context.GetSharedObject(Constants.Config) as ConfigModel;
            if (config == null)
            {
                throw new ApplicationException(string.Format("Key: {0} doesn't exist in build context", Constants.Config));
            }

            string inputPath = StepUtility.GetDoxygenXmlOutputPath(config.OutputPath);
            var processedOutputPath = StepUtility.GetProcessedXmlOutputPath(config.OutputPath);
            if (Directory.Exists(processedOutputPath))
            {
                Directory.Delete(processedOutputPath, recursive: true);
            }
            var dirInfo = Directory.CreateDirectory(processedOutputPath);

            // workaround for Doxygen Bug: it generated xml whose encoding is ANSI while the xml meta is encoding='UTF-8'
            Directory.EnumerateFiles(inputPath, "*.xml").AsParallel().ForAll(
                p =>
                {
                    XDocument doc;
                    using (var fs = File.OpenRead(p))
                    using (var sr = new StreamReader(fs, Encoding.Default))
                    {
                        doc = XDocument.Load(sr);
                    }
                    doc.Save(p);
                });

            // get friendly uid for members
            var memberUidMapping = new ConcurrentDictionary<string, string>();
            await Directory.EnumerateFiles(inputPath, "*.xml").ForEachInParallelAsync(
                p =>
                {
                    XDocument doc = XDocument.Load(p);
                    foreach (var node in doc.XPathSelectElements("//memberdef[@id]"))
                    {
                        var id = node.Attribute("id").Value;
                        memberUidMapping[id] = PreprocessMemberUid(node);
                    }
                    return Task.FromResult(1);
                });

            // workaround for Doxygen Bug: it generated extra namespace for code `public string namespace(){ return ""; }`.
            // so if we find namespace which has same name with class, remove it from index file and also remove its file.
            string indexFile = Path.Combine(inputPath, Constants.IndexFileName);
            XDocument indexDoc = XDocument.Load(indexFile);
            var duplicateItems = (from ele in indexDoc.Root.Elements("compound")
                                  let uid = (string)ele.Attribute("refid")
                                  group ele by RegularizeUid(uid) into g
                                  let duplicate = g.FirstOrDefault(e => (string)e.Attribute("kind") == "namespace")
                                  where g.Count() > 1 && duplicate != null
                                  select (string)duplicate.Attribute("refid")).ToList();

            await Directory.EnumerateFiles(inputPath, "*.xml").ForEachInParallelAsync(
            p =>
            {
                XDocument doc = XDocument.Load(p);
                if (Path.GetFileName(p) == Constants.IndexFileName)
                {
                    var toBeRemoved = (from item in duplicateItems
                                       select doc.XPathSelectElement($"//compound[@refid='{item}']")).ToList();
                    foreach (var element in toBeRemoved)
                    {
                        element.Remove();
                    }
                }
                else if (duplicateItems.Contains(Path.GetFileNameWithoutExtension(p)))
                {
                    return Task.FromResult(1);
                }
                else
                {
                    // workaround for Doxygen Bug: https://bugzilla.gnome.org/show_bug.cgi?id=710175
                    // so if we find package section func/attrib, first check its type, if it starts with `public` or `protected`, move it to related section
                    var toBeMoved = new Dictionary<string, List<XElement>>();
                    var packageMembers = doc.XPathSelectElements("//memberdef[@prot='package']").ToList();
                    foreach (var member in packageMembers)
                    {
                        string kind = (string)member.Parent.Attribute("kind");
                        var type = member.Element("type");
                        string regulized, access;
                        if (type != null && TryRegularizeReturnType(type.CreateNavigator().InnerXml, out regulized, out access))
                        {
                            if (regulized == string.Empty)
                            {
                                type.Remove();
                            }
                            else
                            {
                                type.ReplaceWith(XElement.Parse($"<type>{regulized}</type>"));
                            }
                            member.Attribute("prot").Value = access;
                            var belongToSection = GetSectionKind(access, kind);
                            List<XElement> elements;
                            if (!toBeMoved.TryGetValue(belongToSection, out elements))
                            {
                                elements = new List<XElement>();
                                toBeMoved[belongToSection] = elements;
                            }
                            elements.Add(member);
                            member.Remove();
                        }
                    }
                    foreach (var pair in toBeMoved)
                    {
                        var section = doc.XPathSelectElement($"//sectiondef[@kind='{pair.Key}']");
                        if (section == null)
                        {
                            section = new XElement("sectiondef", new XAttribute("kind", pair.Key));
                            doc.Root.Element("compounddef").Add(section);
                        }
                        foreach (var c in pair.Value)
                        {
                            section.Add(c);
                        }
                    }
                }
                foreach (var node in doc.XPathSelectElements("//node()[@refid]"))
                {
                    node.Attribute("refid").Value = RegularizeUid(node.Attribute("refid").Value, memberUidMapping);
                }
                foreach (var node in doc.XPathSelectElements("//node()[@id]"))
                {
                    node.Attribute("id").Value = RegularizeUid(node.Attribute("id").Value, memberUidMapping);
                }
                doc.Save(Path.Combine(dirInfo.FullName, RegularizeUid(Path.GetFileNameWithoutExtension(p)) + Path.GetExtension(p)));
                return Task.FromResult(1);
            });
        }

        private static string RegularizeUid(string uid, IDictionary<string, string> memberMapping)
        {
            string mapped;
            if (memberMapping.TryGetValue(uid, out mapped))
            {
                return RegularizeUid(mapped);
            }
            return RegularizeUid(uid);
        }

        private static string RegularizeUid(string uid)
        {
            if (uid == null)
            {
                return uid;
            }
            var m = IdRegex.Match(uid);
            if (m.Success)
            {
                uid = m.Groups[2].Value;
            }
            return uid.Replace(Constants.IdSpliter, Constants.Dot);
        }

        private static bool TryRegularizeReturnType(string type, out string regularized, out string access)
        {
            regularized = access = null;
            if (string.IsNullOrEmpty(type))
            {
                return false;
            }
            var match = ToRegularizeTypeRegex.Match(type);
            if (match.Success)
            {
                access = match.Groups[1].Value;
                regularized = type.Replace(match.Groups[0].Value, string.Empty).Trim();
                return true;
            }
            return false;
        }

        private static string PreprocessMemberUid(XElement memberDef)
        {
            StringBuilder builder = new StringBuilder();
            string parentId = memberDef.Ancestors("compounddef").Single().Attribute("id").Value;
            builder.Append(parentId);
            builder.Append(Constants.IdSpliter);
            builder.Append(memberDef.Element("name").Value);
            builder.Append("(");
            var parameters = memberDef.XPathSelectElements("param/type").ToList();
            if (parameters.Count > 0)
            {
                builder.Append(parameters[0].Value);
            }
            foreach (var param in parameters.Skip(1))
            {
                builder.Append("," + param.Value);
            }
            builder.Append(")");
            return builder.ToString();
        }

        private static string GetSectionKind(string access, string kind)
        {
            var splits = kind.Split(new char[] { '-' });
            splits[0] = access;
            return string.Join("-", splits);
        }
    }
}