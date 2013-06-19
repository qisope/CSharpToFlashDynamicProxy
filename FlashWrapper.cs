using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

using AxShockwaveFlashObjects;

using log4net;

using Qisope.Utils.Delegates;
using Qisope.Views.App;

namespace Qisope
{
	public class FlashWrapper : IDisposable
	{
		private readonly ILog _log;
		private AxFlashControl _axFlashControl;
		private Form _hostForm;

		public FlashWrapper(AxFlashControl axFlashControl)
		{
			_log = LogManager.GetLogger(GetType());
			_axFlashControl = axFlashControl;

			if (_axFlashControl.IsHandleCreated)
			{
				Initialize(_axFlashControl);
			}
			else
			{
				_log.Debug("Handle is not created for AxFlashControl, wiring HandleCreated event");
				_axFlashControl.HandleCreated += AxFlashControlOnHandleCreated;
			}
		}

		public Point LocationOnForm
		{
			get
			{
				if (_axFlashControl == null)
				{
					return Point.Empty;
				}

				if (_hostForm == null)
				{
					_hostForm = _axFlashControl.FindForm();

					if (_hostForm == null)
					{
						return Point.Empty;
					}
				}

				Point flashPointToScreen = _axFlashControl.PointToScreen(_axFlashControl.Location);
				Point hostFormLocation = _hostForm.Location;
				return new Point(flashPointToScreen.X - hostFormLocation.X, flashPointToScreen.Y - hostFormLocation.Y);
			}
		}

		public Rectangle BoundsOnControl
		{
			get { return new Rectangle(LocationOnForm.X, LocationOnForm.Y, _axFlashControl.Width, _axFlashControl.Height); }
		}

		#region IDisposable Members

		public void Dispose()
		{
			if (_axFlashControl != null)
			{
				_axFlashControl.HandleCreated -= AxFlashControlOnHandleCreated;
				_axFlashControl.FlashCall -= AxShockwaveFlashOnFlashCall;
				_axFlashControl.PreviewKeyDown -= AxShockwaveFlashPreviewKeyDown;
				_axFlashControl = null;
			}

			_hostForm = null;

			FlashCall = null;
			UnhandledFlashCallback = null;
			PreviewKeyDown = null;
		}

		#endregion

		public event PreviewKeyDownEventHandler PreviewKeyDown;
		public event EventHandler<FlashCallEventArgs> FlashCall;
		public event Action<string> UnhandledFlashCallback;

		public void Initialize(AxFlashControl axFlashControl)
		{
			_axFlashControl.FlashCall += AxShockwaveFlashOnFlashCall;
			_axFlashControl.PreviewKeyDown += AxShockwaveFlashPreviewKeyDown;

			_hostForm = _axFlashControl.FindForm();
		}

		private void AxFlashControlOnHandleCreated(object sender, EventArgs args)
		{
			if (_axFlashControl.IsHandleCreated)
			{
				Initialize(_axFlashControl);
			}
		}

		private void AxShockwaveFlashOnFlashCall(object sender, _IShockwaveFlashEvents_FlashCallEvent e)
		{
			EventHandler<FlashCallEventArgs> eventHandler = FlashCall;
			if (eventHandler == null)
			{
				return;
			}

			var args = new FlashCallEventArgs(e, false);
			eventHandler(sender, args);

			if (!args.Handled && UnhandledFlashCallback != null)
			{
				UnhandledFlashCallback(e.request);
			}
		}

		private void AxShockwaveFlashPreviewKeyDown(object sender, PreviewKeyDownEventArgs e)
		{
			if (PreviewKeyDown != null)
			{
				PreviewKeyDown(sender, e);
			}
		}

		public string CallFunction(string methodInvokeMessage)
		{
			try
			{
				if (_axFlashControl == null || !_axFlashControl.IsHandleCreated || _axFlashControl.IsDisposed)
				{
					return "<undefined/>";
				}

				string result;

				if (_axFlashControl.InvokeRequired)
				{
					Func<string, string> callFunction = _axFlashControl.CallFunction;
					result = _axFlashControl.Invoke(callFunction, methodInvokeMessage) as string;
				}
				else
				{
					result = _axFlashControl.CallFunction(methodInvokeMessage);
				}

				return result;
			}
			catch (COMException)
			{
				_log.WarnFormat("Flash Method Not Implemented: '{0}'", methodInvokeMessage);
				return "<undefined/>";
			}
			catch (Exception e)
			{
				if (_axFlashControl == null || !_axFlashControl.IsHandleCreated || _axFlashControl.IsDisposed)
				{
					_log.Warn("Control was disposed or otherwise killed before call");
					return "<undefined/>";
				}
				
				_log.ErrorFormat("Unhandled exception: {0}", e);
				throw;
			}
		}

		public void SetReturnValue(string returnXml)
		{
			if (_axFlashControl != null)
			{
				_axFlashControl.SetReturnValue(returnXml);
			}
		}
	}
}