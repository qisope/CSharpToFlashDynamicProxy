using System;

using Qisope.Utils.Delegates;
using Qisope.Views.App;

namespace Qisope
{
	public abstract class FlashInterfaceBase: IDisposable
	{
		public FlashControl FlashInstance { get; set; }
		public event Action FlashInitialized;

		protected void InvokeFlashInitialized()
		{
			Action initialized = FlashInitialized;
			if (initialized != null)
			{
				initialized();
			}
		}

		public virtual void Dispose()
		{
			FlashProxyFactory.Unregister(this);
		}
	}
}