using AxShockwaveFlashObjects;

namespace Qisope
{
	public interface IFlashProxyFactory
	{
		T CreateProxy<T>(FlashWrapper flashWrapper, string flashSessionID) where T : FlashInterfaceBase;
	}
}