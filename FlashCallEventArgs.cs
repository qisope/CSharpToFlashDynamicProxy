using System.ComponentModel;

using AxShockwaveFlashObjects;

namespace Qisope
{
	public class FlashCallEventArgs : HandledEventArgs
	{
		public FlashCallEventArgs(string request)
		{
			Request = request;
		}

		public FlashCallEventArgs(_IShockwaveFlashEvents_FlashCallEvent e, bool isHandled)
				: base(isHandled)
		{
			Request = e.request;
		}

		public string Request { get; private set; }
	}
}