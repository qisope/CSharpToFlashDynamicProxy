using System;

namespace Qisope
{
	[AttributeUsage(AttributeTargets.Method)]
	public class FlashCallbackAttribute: Attribute
	{
		public string CallbackMethodName { get; set; }
	}
}