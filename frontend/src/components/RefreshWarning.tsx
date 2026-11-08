/** Shown above data that is still on screen but could not be refreshed: the user knows it may be out of date. */
export default function RefreshWarning({ what, reason }: { what: string; reason: string }) {
  return (
    <p className="warning" role="status">
      {what} could not be refreshed ({reason}) and may be out of date. Trying again automatically.
    </p>
  )
}
