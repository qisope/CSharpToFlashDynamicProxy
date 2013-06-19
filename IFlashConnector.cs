using System.Reflection;

namespace Qisope
{
	public interface IFlashConnector
	{
		object InvokeFlashMethod(string methodName, string methodReturnTypeName, params object[] args);
		void AddCallback(string callbackMethodName, MethodInfo targetMethodInfo);
		void RegisterFlashInterface(FlashInterfaceBase flashInterface);
		void Unregister();
	}
}