using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Xml;

using log4net;

using Newtonsoft.Json;

using Qisope.Settings;
using Qisope.Utils;

namespace Qisope
{
	public class FlashConnector : IFlashConnector
	{
		private const string F_NUM = "number";
		private const string F_STR = "string";
		private const string F_VOID = "void";
		public const string INVOKE_FLASH_METHOD_NAME = "InvokeFlashMethod";
		private const string FLASH_CONNECTOR_UNREGISTERED = "FlashConnector instance has been unregistered";

		private static readonly Dictionary<Type, string> s_flashTypeTable;
		private static readonly XmlReaderSettings s_xmlReaderSettings;
		private static readonly XmlWriterSettings s_xmlWriterSettings;
		private readonly Dictionary<string, MethodInfo> _callbackTable;
		private readonly string _flashSessionID;
		private FlashWrapper _flashWrapper;
		private readonly ILog _log;
		private bool _flashCallingOut;
		private FlashInterfaceBase _flashInterface;

		static FlashConnector()
		{
			s_flashTypeTable = new Dictionary<Type, string>
			                  {
			                  		{typeof (byte), F_NUM},
			                  		{typeof (Int16), F_NUM},
			                  		{typeof (UInt16), F_NUM},
			                  		{typeof (Int32), F_NUM},
			                  		{typeof (UInt32), F_NUM},
			                  		{typeof (Int64), F_NUM},
			                  		{typeof (UInt64), F_NUM},
			                  		{typeof (float), F_NUM},
			                  		{typeof (double), F_NUM},
			                  		{typeof (string), F_STR},
			                  		{typeof (void), F_VOID},
			                  		{typeof (DateTime), F_STR}
			                  };

			s_xmlReaderSettings = new XmlReaderSettings {IgnoreComments = true, IgnoreProcessingInstructions = true, IgnoreWhitespace = true};

			s_xmlWriterSettings = new XmlWriterSettings {Indent = false, NewLineChars = "\n", OmitXmlDeclaration = true};

			ObjectSerializationMode = ObjectSerializationModeEnum.JSON;
		}

		public FlashConnector(FlashWrapper flashWrapper, string flashSessionID)
		{
			_flashWrapper = flashWrapper;
			_flashSessionID = flashSessionID ?? string.Empty;
			_callbackTable = new Dictionary<string, MethodInfo>();
			_log = LogManager.GetLogger(GetType());

			if (flashWrapper != null)
			{
				_flashWrapper.FlashCall += FlashWrapper_FlashCall;
			}
		}

		public static ObjectSerializationModeEnum ObjectSerializationMode { get; set; }

		#region IFlashConnector Members

		public void RegisterFlashInterface(FlashInterfaceBase flashInterface)
		{
			_flashInterface = flashInterface;

			using (NDC.Push(_flashSessionID))
			{
				_log.InfoFormat("Registering Flash Interface Type '{0}'", flashInterface.GetType().FullName);
			}
		}

		public void Unregister()
		{
			if (_flashWrapper != null)
			{
				_flashWrapper.FlashCall -= FlashWrapper_FlashCall;
				_flashWrapper = null;
			}
		}

		public virtual object InvokeFlashMethod(string methodName, string methodReturnTypeName, params object[] args)
		{
			if (_flashWrapper == null)
			{
				_log.Error(FLASH_CONNECTOR_UNREGISTERED);
				throw new Exception(FLASH_CONNECTOR_UNREGISTERED);
			}

			if (UserSettings.Default.LogFlash && _flashCallingOut)
			{
				_log.WarnFormat("Method '{0}' is calling into Flash while another method has called out.", methodName);
			}

			using (NDC.Push(_flashSessionID))
			{
				if (UserSettings.Default.LogFlash)
				{
					if (_log.IsDebugEnabled)
					{
						var parameters = new List<string>();
						foreach (var arg in args)
						{
							parameters.Add(string.Format("{0} {1}", arg == null ? "<null>" : arg.GetType().Name, arg ?? "null"));
						}
						_log.DebugFormat("Invoking flash method '{0} {1}({2})'", methodReturnTypeName, methodName, string.Join(", ", parameters.ToArray()));
					}
					else
					{
						_log.InfoFormat("Invoking flash method '{0}'", methodName);
					}
				}

				var methodReturnType = Type.GetType(methodReturnTypeName);
				var methodInvokeMessage = SerializeMethodInvokeMessage(methodName, methodReturnType, args);

				var result = _flashWrapper.CallFunction(methodInvokeMessage);

				var returnTypeIsVoid = methodReturnType == typeof (void);

				if (UserSettings.Default.LogFlash && !returnTypeIsVoid)
				{
					_log.DebugFormat("InvokeFlashMethod: '{0}' returning type '{1}' with value '{2}'", methodName, methodReturnTypeName, result ?? "<null>");
				}

				return returnTypeIsVoid ? null : Deserialize(result, methodReturnType);
			}
		}

		public void AddCallback(string callbackMethodName, MethodInfo targetMethodInfo)
		{
			_callbackTable.Add(callbackMethodName.ToLower(), targetMethodInfo);
		}

		#endregion

		private void FlashWrapper_FlashCall(object sender, FlashCallEventArgs args)
		{
			_flashCallingOut = true;
			try
			{
				if (args.Handled)
				{
					return; // Already handled
				}

				using (NDC.Push(_flashSessionID))
				{
					string methodName;
					object[] parameters;
					if (!TryDeserializeMethodInvokeMessage(args.Request, out methodName, out parameters))
					{
						return; // We don't know of this method
					}

					MethodInfo targetMethodInfo;
					if (!_callbackTable.TryGetValue(methodName.ToLower(), out targetMethodInfo))
					{
						_log.ErrorFormat("Did not find a .NET callback method named '{0}'", methodName);
						return;
					}

					object result;
					try
					{
						_log.DebugFormat("Flash Invoking method '{0}'", methodName);
						result = _flashInterface.GetType().InvokeMember(targetMethodInfo.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.InvokeMethod, null, _flashInterface, parameters);
					}
					catch (Exception ex)
					{
						_log.ErrorFormat("Exception invoking '{0}' from Flash: {1}", methodName, ex);
						throw;
					}

					if (targetMethodInfo.ReturnType == typeof(void))
					{
						if (UserSettings.Default.LogFlash && !_log.IsDebugEnabled)
						{
							_log.InfoFormat("Flash invoked method '{0}'", methodName);
						}
						args.Handled = true;
						_flashWrapper.SetReturnValue("<undefined/>");
						return;
					}

					var stringBuilder = new StringBuilder();
					using (var xmlWriter = XmlWriter.Create(stringBuilder, s_xmlWriterSettings))
					{
						Serialize(xmlWriter, result);
					}

					var returnXml = stringBuilder.ToString();
					_flashWrapper.SetReturnValue(returnXml);

					if (UserSettings.Default.LogFlash)
					{
						if (_log.IsDebugEnabled)
						{
							_log.DebugFormat("Flash invoked method '{0}' returning xml value '{1}'", methodName, returnXml);
						}
						else
						{
							_log.InfoFormat("Flash invoked method '{0}' returning '{1}'", methodName, result ?? "<null>");
						}
					}

					args.Handled = true;
				}
			}
			finally
			{
				_flashCallingOut = false;
			}
		}

		public static string Serialize(object value)
		{
			var sb = new StringBuilder();
			using (var xmlWriter = XmlWriter.Create(sb, s_xmlWriterSettings))
			{
				Serialize(xmlWriter, value);
			}

			return sb.ToString();
		}

		public static void Serialize(XmlWriter xmlWriter, object value)
		{
			if (value == null)
			{
				xmlWriter.WriteStartElement("null");
				xmlWriter.WriteEndElement();
				return;
			}

			Type type = value.GetType();

			if (type == typeof (bool))
			{
				xmlWriter.WriteStartElement(value.ToString().ToLower());
				xmlWriter.WriteEndElement();
			}
			else if (type.IsPrimitive || type == typeof (DateTime))
			{
				SerializePrimitive(xmlWriter, type, value);
			}
			else if (type == typeof (string))
			{
				SerializeString(xmlWriter, (string)value);
			}
			else if (type == typeof(byte[]))
			{
				SerializeByteArray(xmlWriter, (byte[])value);
			}
			else
			{
				if (ObjectSerializationMode == ObjectSerializationModeEnum.FlashXml)
				{
					if (type.IsArray)
					{
						SerializeArray(xmlWriter, value);
					}
					else
					{
						SerializeStructuredObject(xmlWriter, type, value);
					}
				}
				else
				{
					SerializeAsJSON(xmlWriter, value);
				}
			}
		}

		private static void SerializePrimitive(XmlWriter xmlWriter, Type type, object value)
		{
			string flashType;
			if (!s_flashTypeTable.TryGetValue(type, out flashType))
			{
				throw new Exception("Unsupported primitive type: " + type.FullName);
			}

			xmlWriter.WriteStartElement(flashType);
			xmlWriter.WriteValue(value);
			xmlWriter.WriteEndElement();
		}

		private static void SerializeString(XmlWriter xmlWriter, string value)
		{
			xmlWriter.WriteStartElement("string");
			xmlWriter.WriteCData(value);
			xmlWriter.WriteEndElement();
		}

		private static void SerializeArray(XmlWriter xmlWriter, object value)
		{
			var array = (Array)value;

			xmlWriter.WriteStartElement("array");

			for (int i = 0; i < array.Length; i++)
			{
				object item = array.GetValue(i);
				xmlWriter.WriteStartElement("property");
				xmlWriter.WriteAttributeString("id", i.ToString());
				Serialize(xmlWriter, item);
				xmlWriter.WriteEndElement();
			}

			xmlWriter.WriteEndElement();
		}

		private static void SerializeByteArray(XmlWriter xmlWriter, byte[] value)
		{
			xmlWriter.WriteStartElement("string");

			if (value != null && value.Length > 0)
			{
				xmlWriter.WriteCData(Convert.ToBase64String(value));
			}
			xmlWriter.WriteEndElement();
		}

		private static void SerializeStructuredObject(XmlWriter xmlWriter, Type type, object value)
		{
			PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

			xmlWriter.WriteStartElement("object");

			foreach (var property in properties)
			{
				object[] nonSerializedAttributes = property.GetCustomAttributes(typeof (NonSerializedAttribute), true);

				if (nonSerializedAttributes.Length == 0)
				{
					xmlWriter.WriteStartElement("property");
					xmlWriter.WriteAttributeString("id", property.Name);
					object propertyValue = property.GetValue(value, null);
					Serialize(xmlWriter, propertyValue);
					xmlWriter.WriteEndElement();
				}
			}

			xmlWriter.WriteEndElement();
		}

		private static void SerializeAsJSON(XmlWriter xmlWriter, object value)
		{
			var sb = new StringBuilder();
			using (var sw = new StringWriter(sb))
			{
				var ser = JsonUtils.NewFlashJsonSerializer();
				ser.Serialize(sw, value);
			}

			SerializeString(xmlWriter, sb.ToString());
		}

		public static object Deserialize(string data, Type type)
		{
			var sr = new StringReader(data);
			XmlReader xmlReader = XmlReader.Create(sr, s_xmlReaderSettings);
			xmlReader.MoveToContent();
			return Deserialize(xmlReader, type);
		}

		private static object Deserialize(XmlReader xmlReader, Type type)
		{
			if (xmlReader.Name == "null" || xmlReader.Name == "undefined")
			{
				xmlReader.Read();
				return null;
			}

			if (type.IsPrimitive || type == typeof (string) || type == typeof (DateTime))
			{
				return DeserializePrimitive(xmlReader, type);
			}

			if (type == typeof (byte[]))
			{
				return DeserializeByteArray(xmlReader);
			}

			if (type.IsArray)
			{
				return DeserializeArray(xmlReader, type);
			}

			object result;

			if (ObjectSerializationMode == ObjectSerializationModeEnum.FlashXml)
			{
				if (xmlReader.Name != "object")
				{
					throw new FormatException("Expected an object element");
				}

				result = Activator.CreateInstance(type);

				if (xmlReader.ReadToDescendant("property"))
				{
					do
					{
						if (!xmlReader.MoveToAttribute("id"))
						{
							throw new FormatException("Expected an id attribute");
						}

						string propertyName = xmlReader.Value;
						PropertyInfo property = type.GetProperty(propertyName);
						xmlReader.Read();
						object value = Deserialize(xmlReader, property.PropertyType);
						property.SetValue(result, value, null);
					} while (xmlReader.ReadToNextSibling("property"));
				}

				xmlReader.ReadEndElement();
			}
			else
			{
				if (xmlReader.Name != "string")
				{
					throw new FormatException("Expected a string element containing JSON data");
				}

				var jsonData = (string)DeserializePrimitive(xmlReader, typeof (string));

				using (var sr = new StringReader(jsonData))
				{
					using (var jr = new JsonReader(sr))
					{
						var ser = JsonUtils.NewFlashJsonSerializer();
						result = ser.Deserialize(jr, type);
					}
				}
			}

			return result;
		}

		private static object DeserializePrimitive(XmlReader xmlReader, Type type)
		{
			if (!xmlReader.IsEmptyElement)
			{
				var name = xmlReader.Name;
				xmlReader.Read();

				object value = null;

				if (xmlReader.NodeType == XmlNodeType.CDATA)
				{
					value = xmlReader.Value;
					xmlReader.Read();
				}
				else if (xmlReader.NodeType == XmlNodeType.Text)
				{
					if (name == F_NUM)
					{
						var @float = xmlReader.ReadContentAsFloat();
						value = Convert.ChangeType(@float, type);
					}
					else
					{
						value = xmlReader.ReadContentAs(type, null);

						if (value is string)
						{
							string valueString = (string)value;
							valueString = valueString.Trim();

							// for some reason the cdata check above doesn't appear to work
							if (valueString.StartsWith("<![CDATA[") && valueString.EndsWith("]]>"))
							{
								value = valueString.Substring(9, valueString.Length - 12);
							}
						}
					}
				}

				xmlReader.ReadEndElement();
				return value;
			}

			if (type == typeof (bool))
			{
				var value = xmlReader.Name;
				bool result;
                xmlReader.Read();
                if (bool.TryParse(value, out result))
				{
					return result;
				}
			}

			return null;
		}

		private static Array DeserializeArray(XmlReader xmlReader, Type type)
		{
			if (xmlReader.Name != "array")
			{
				throw new FormatException("Expected an array element");
			}

			Type elementType = type.GetElementType();
			var result = new List<object>();

			if (xmlReader.ReadToDescendant("property"))
			{
				do
				{
					xmlReader.Read();
					object value = Deserialize(xmlReader, elementType);
					result.Add(value);
				} while (xmlReader.ReadToNextSibling("property"));
			}

			xmlReader.ReadEndElement();

			Array array = Array.CreateInstance(elementType, result.Count);

			for (int i = 0; i < result.Count; i++)
			{
				object item = result[i];
				array.SetValue(item, i);
			}

			return array;
		}

		private static byte[] DeserializeByteArray(XmlReader xmlReader)
		{
			if (xmlReader.Name != "string")
			{
				throw new FormatException("Expected a string element");
			}

			if (xmlReader.IsEmptyElement)
			{
				throw new FormatException("Empty element not expected");
			}

			xmlReader.Read();

			if (xmlReader.NodeType != XmlNodeType.Text)
			{
				throw new FormatException("Expected a text value");
			}

			var value = xmlReader.ReadContentAsString();

			xmlReader.ReadEndElement();

			if (string.IsNullOrEmpty(value))
			{
				return null;
			}

			return Convert.FromBase64String(value);
		}

		public static string SerializeMethodInvokeMessage(string methodName, Type methodReturnType, params object[] parameters)
		{
			var stringBuilder = new StringBuilder();
			using (var xmlWriter = XmlWriter.Create(stringBuilder, s_xmlWriterSettings))
			{
				Debug.Assert(xmlWriter != null);
				xmlWriter.WriteStartElement("invoke");
				xmlWriter.WriteAttributeString("name", methodName);
				var flashReturnType = methodReturnType == typeof (void) ? "void" : "xml";
				xmlWriter.WriteAttributeString("returntype", flashReturnType);

				if (parameters.Length > 0)
				{
					xmlWriter.WriteStartElement("arguments");

					foreach (var parameter in parameters)
					{
						Serialize(xmlWriter, parameter);
					}

					xmlWriter.WriteEndElement();
				}

				xmlWriter.WriteEndElement();
			}

			return stringBuilder.ToString();
		}

		internal bool TryDeserializeMethodInvokeMessage(string methodInvokeMessage, out string methodName, out object[] parameters)
		{
			try
			{
				using (var sr = new StringReader(methodInvokeMessage))
				{
					using (var xmlReader = XmlReader.Create(sr))
					{
						xmlReader.ReadToFollowing("invoke");
						xmlReader.MoveToAttribute("name");
						methodName = xmlReader.ReadContentAsString();

						MethodInfo methodInfo;
						if (_callbackTable.TryGetValue(methodName.ToLower(), out methodInfo))
						{
							Type[] parameterTypes = Array.ConvertAll(methodInfo.GetParameters(), input => input.ParameterType);
							parameters = new object[parameterTypes.Length];

							if (xmlReader.ReadToFollowing("arguments"))
							{
								if (parameterTypes.Length > 0 && xmlReader.Read())
								{
									for (int i = 0; i < parameterTypes.Length; i++)
									{
										var parameterType = parameterTypes[i];
										parameters[i] = Deserialize(xmlReader, parameterType);
									}
								}
							}

							return true;
						}
					}
				}
			}
			catch(Exception e)
			{
				_log.Fatal("Exception deserializing the message from Flash: {0}", e);
				throw;
			}

			parameters = null;
			return false;
		}
	}

	public enum ObjectSerializationModeEnum
	{
		FlashXml,
		JSON,
	}
}