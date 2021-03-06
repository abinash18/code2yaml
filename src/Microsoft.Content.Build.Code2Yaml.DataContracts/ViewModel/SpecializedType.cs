﻿namespace Microsoft.Content.Build.Code2Yaml.DataContracts
{
    using System;
    using YamlDotNet.Serialization;

    [Serializable]
    public class SpecializedType
    {
        [YamlMember(Alias = "type")]
        public string Type { get; set; }

        [YamlMember(Alias = "name")]
        public string SpecializedFullName { get; set; }
    }
}
