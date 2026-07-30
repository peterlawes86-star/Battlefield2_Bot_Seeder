using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace BF2BotManager
{
    [XmlRoot("bf2bot")]
    public class BotConfig
    {
        [XmlElement("server")]
        public ServerConfig Server { get; set; } = new ServerConfig();

        public static BotConfig LoadFromFile(string path)
        {
            var serializer = new XmlSerializer(typeof(BotConfig));
            using (var reader = new StreamReader(path))
            {
                var result = serializer.Deserialize(reader) as BotConfig;
                return result ?? new BotConfig();
            }
        }

        public void SaveToFile(string path)
        {
            var serializer = new XmlSerializer(typeof(BotConfig));
            using (var writer = new StreamWriter(path))
            {
                serializer.Serialize(writer, this);
            }
        }
    }

    public class ServerConfig
    {
        [XmlAttribute("address")]
        public string Address { get; set; } = "51.161.201.123";

        [XmlAttribute("port")]
        public int Port { get; set; } = 16567;

        [XmlAttribute("loginserver")]
        public string LoginServer { get; set; } = "gpcm.gamespy.com";

        [XmlAttribute("mod")]
        public string Mod { get; set; } = "mods/bf2";

        [XmlAttribute("autoreconnect")]
        public bool AutoReconnect { get; set; } = false;

        [XmlElement("client")]
        public List<ClientConfig> Clients { get; set; } = new List<ClientConfig>();
    }

    public class ClientConfig
    {
        [XmlAttribute("nickname")]
        public string Nickname { get; set; } = string.Empty;

        [XmlAttribute("password")]
        public string Password { get; set; } = string.Empty;

        [XmlAttribute("cdkey")]
        public string CDKey { get; set; } = string.Empty;
    }
}