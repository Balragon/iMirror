namespace MacMirrorReceiver;

internal sealed class WindowLifecycleState
{
	private bool _hideNotificationShown;

	public bool IsExplicitExit { get; private set; }

	public bool ShouldHideOnClose()
	{
		return !IsExplicitExit;
	}

	public void MarkExplicitExit()
	{
		IsExplicitExit = true;
	}

	public bool ConsumeFirstHideNotification()
	{
		if (_hideNotificationShown)
		{
			return false;
		}

		_hideNotificationShown = true;
		return true;
	}
}
