using System;

namespace Qisope
{
	[AttributeUsage(AttributeTargets.Method)]
	public class FlashMethodAttribute: Attribute
	{
		public string FlashMethodName { get; set; }
	}
}