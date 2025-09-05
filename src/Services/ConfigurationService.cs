using System;
using System.IO;
using System.Xml.Serialization;
using ExHyperV.Models;

namespace ExHyperV.Services
{
    public class ConfigurationService
    {
        private readonly string _configFilePath;

        public ConfigurationService()
        {
            // 将 config.xml 定位在应用程序的根目录下
            _configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.xml");
        }

        public AppConfig LoadConfiguration()
        {
            if (!File.Exists(_configFilePath))
            {
                // 如果文件不存在，返回一个全新的、空的配置对象
                return new AppConfig();
            }

            try
            {
                var serializer = new XmlSerializer(typeof(AppConfig));
                using (var reader = new StreamReader(_configFilePath))
                {
                    return (AppConfig)serializer.Deserialize(reader);
                }
            }
            catch (Exception ex)
            {
                // 如果文件损坏或格式错误，记录日志并返回一个空配置
                System.Diagnostics.Debug.WriteLine($"Error loading config.xml: {ex.Message}");
                return new AppConfig();
            }
        }

        public void SaveConfiguration(AppConfig config)
        {
            try
            {
                var serializer = new XmlSerializer(typeof(AppConfig));
                using (var writer = new StreamWriter(_configFilePath, false)) // false 表示覆盖文件
                {
                    serializer.Serialize(writer, config);
                }
            }
            catch (Exception ex)
            {
                // 处理保存失败的情况
                System.Diagnostics.Debug.WriteLine($"Error saving config.xml: {ex.Message}");
            }
        }
    }
}