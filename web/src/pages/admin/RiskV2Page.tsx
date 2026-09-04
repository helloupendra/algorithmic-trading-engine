import { useState, useEffect } from 'react'
import {
  ExclamationTriangleIcon as ShieldAlert,
  StopIcon as Power,
  LockClosedIcon as Shield,
  ActivityLogIcon as Activity,
  ExclamationTriangleIcon as AlertOctagon,
  ClockIcon as Clock,
  PersonIcon as UserIcon,
  UpdateIcon as Save,
  CheckCircledIcon as CheckCircle2
} from '@radix-ui/react-icons'
import {
  useKillSwitch,
  useSetKillSwitch,
  useRiskLimits,
  useUpdateRiskLimits,
  useRiskEvents
} from '../../lib/queries'

function classNames(...classes: (string | undefined | null | false)[]) {
  return classes.filter(Boolean).join(' ')
}

export function RiskV2Page() {
  const { data: killSwitch } = useKillSwitch()
  const { data: limits } = useRiskLimits()
  const { data: events } = useRiskEvents(50)

  const { mutate: setKillSwitch, isPending: isSettingKillSwitch } = useSetKillSwitch()
  const { mutate: updateLimits, isPending: isUpdatingLimits } = useUpdateRiskLimits()

  const [haltReason, setHaltReason] = useState('')
  const [resumeReason, setResumeReason] = useState('')
  const [showLimitsForm, setShowLimitsForm] = useState(false)

  // Local state for limits
  const [localLimits, setLocalLimits] = useState({
    maxOrdersPerMinute: 0,
    maxDailyLoss: 0,
    maxConcurrentRuns: 0,
    maxRunsPerUser: 0
  })

  // Sync from query to local state
  useEffect(() => {
    if (limits && !showLimitsForm) {
      setLocalLimits({
        maxOrdersPerMinute: limits.maxOrdersPerMinute,
        maxDailyLoss: limits.maxDailyLoss,
        maxConcurrentRuns: limits.maxConcurrentRuns,
        maxRunsPerUser: limits.maxRunsPerUser
      })
    }
  }, [limits, showLimitsForm])

  const handleActivate = (e: React.FormEvent) => {
    e.preventDefault()
    if (!haltReason.trim()) return
    if (!window.confirm('WARNING: This will flatten all open positions and halt all new orders globally. Proceed?')) {
      return
    }
    setKillSwitch(
      { activate: true, reason: haltReason },
      {
        onSuccess: () => setHaltReason('')
      }
    )
  }

  const handleDeactivate = (e: React.FormEvent) => {
    e.preventDefault()
    if (!resumeReason.trim()) return
    setKillSwitch(
      { activate: false, reason: resumeReason },
      {
        onSuccess: () => setResumeReason('')
      }
    )
  }

  const handleSaveLimits = (e: React.FormEvent) => {
    e.preventDefault()
    updateLimits(localLimits, {
      onSuccess: () => setShowLimitsForm(false)
    })
  }

  return (
    <div className="space-y-6 max-w-7xl mx-auto p-4 sm:p-6 lg:p-8">
      <div>
        <h1 className="text-2xl font-bold text-gray-100 flex items-center gap-2">
          <ShieldAlert className="w-6 h-6 text-red-500" />
          Global Risk Management
        </h1>
        <p className="mt-1 text-sm text-gray-400">
          Platform-wide kill switch, global limits, and live risk event audit trail.
        </p>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {/* KILL SWITCH CARD */}
        <div className="bg-[#1C2127] border border-gray-800 rounded-lg overflow-hidden flex flex-col">
          <div className="px-6 py-4 border-b border-gray-800 flex justify-between items-center bg-black/20">
            <h2 className="text-lg font-medium text-gray-100 flex items-center gap-2">
              <Power className="w-5 h-5 text-gray-400" />
              Kill Switch
            </h2>
            {killSwitch?.isActive ? (
              <span className="inline-flex items-center gap-1.5 px-3 py-1 rounded-full text-xs font-medium bg-red-500/10 text-red-400 border border-red-500/20">
                <AlertOctagon className="w-3.5 h-3.5" />
                ACTIVE (TRADING HALTED)
              </span>
            ) : (
              <span className="inline-flex items-center gap-1.5 px-3 py-1 rounded-full text-xs font-medium bg-green-500/10 text-green-400 border border-green-500/20">
                <CheckCircle2 className="w-3.5 h-3.5" />
                DORMANT (TRADING ALLOWED)
              </span>
            )}
          </div>

          <div className="p-6 flex-1 flex flex-col">
            <p className="text-sm text-gray-400 mb-6">
              When activated, every strategy runner receives an immediate shutdown command, all open positions are flattened at market, and all new orders are rejected.
            </p>

            {killSwitch?.isActive ? (
              <form onSubmit={handleDeactivate} className="mt-auto space-y-4">
                <div className="p-4 rounded-md bg-red-500/5 border border-red-500/20 mb-6">
                  <p className="text-sm text-red-400 font-medium mb-1">Last halted by {killSwitch.updatedBy || 'system'}</p>
                  <p className="text-sm text-gray-300">Reason: "{killSwitch.reason}"</p>
                  <p className="text-xs text-gray-500 mt-2">{new Date(killSwitch.updatedUtc || '').toLocaleString()}</p>
                </div>

                <div>
                  <label htmlFor="resumeReason" className="block text-sm font-medium text-gray-300 mb-1">
                    Reason for resuming trading
                  </label>
                  <input
                    type="text"
                    id="resumeReason"
                    required
                    value={resumeReason}
                    onChange={(e) => setResumeReason(e.target.value)}
                    className="block w-full rounded-md border-gray-700 bg-gray-800 text-gray-100 focus:border-emerald-500 focus:ring-emerald-500 sm:text-sm"
                    placeholder="e.g., Issue resolved, resuming normal operations"
                  />
                </div>
                <button
                  type="submit"
                  disabled={isSettingKillSwitch || !resumeReason.trim()}
                  className="w-full flex justify-center py-2 px-4 border border-transparent rounded-md shadow-sm text-sm font-medium text-white bg-emerald-600 hover:bg-emerald-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-emerald-500 focus:ring-offset-[#1C2127] disabled:opacity-50 disabled:cursor-not-allowed"
                >
                  {isSettingKillSwitch ? 'Deactivating...' : 'DEACTIVATE KILL SWITCH'}
                </button>
              </form>
            ) : (
              <form onSubmit={handleActivate} className="mt-auto space-y-4">
                <div>
                  <label htmlFor="haltReason" className="block text-sm font-medium text-gray-300 mb-1">
                    Reason for halting trading
                  </label>
                  <input
                    type="text"
                    id="haltReason"
                    required
                    value={haltReason}
                    onChange={(e) => setHaltReason(e.target.value)}
                    className="block w-full rounded-md border-gray-700 bg-gray-800 text-gray-100 focus:border-red-500 focus:ring-red-500 sm:text-sm"
                    placeholder="e.g., Unexpected market volatility"
                  />
                </div>
                <button
                  type="submit"
                  disabled={isSettingKillSwitch || !haltReason.trim()}
                  className="w-full flex justify-center py-2 px-4 border border-transparent rounded-md shadow-sm text-sm font-medium text-white bg-red-600 hover:bg-red-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-red-500 focus:ring-offset-[#1C2127] disabled:opacity-50 disabled:cursor-not-allowed"
                >
                  {isSettingKillSwitch ? 'Activating...' : 'ACTIVATE KILL SWITCH'}
                </button>
              </form>
            )}
          </div>
        </div>

        {/* GLOBAL LIMITS CARD */}
        <div className="bg-[#1C2127] border border-gray-800 rounded-lg overflow-hidden flex flex-col">
          <div className="px-6 py-4 border-b border-gray-800 flex justify-between items-center bg-black/20">
            <h2 className="text-lg font-medium text-gray-100 flex items-center gap-2">
              <Shield className="w-5 h-5 text-gray-400" />
              Global Risk Limits
            </h2>
            {!showLimitsForm && (
              <button
                onClick={() => setShowLimitsForm(true)}
                className="text-sm text-blue-400 hover:text-blue-300 font-medium"
              >
                Edit
              </button>
            )}
          </div>

          <div className="p-6 flex-1">
            {showLimitsForm ? (
              <form onSubmit={handleSaveLimits} className="space-y-4">
                <div className="grid grid-cols-2 gap-4">
                  <div>
                    <label className="block text-sm font-medium text-gray-400 mb-1">Max Orders / Min</label>
                    <input
                      type="number"
                      required
                      min={0}
                      value={localLimits.maxOrdersPerMinute}
                      onChange={(e) => setLocalLimits({ ...localLimits, maxOrdersPerMinute: parseInt(e.target.value) })}
                      className="block w-full rounded-md border-gray-700 bg-gray-800 text-gray-100 focus:border-blue-500 focus:ring-blue-500 sm:text-sm"
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-400 mb-1">Max Daily Loss (₹)</label>
                    <input
                      type="number"
                      required
                      max={0}
                      step={100}
                      value={localLimits.maxDailyLoss}
                      onChange={(e) => setLocalLimits({ ...localLimits, maxDailyLoss: parseFloat(e.target.value) })}
                      className="block w-full rounded-md border-gray-700 bg-gray-800 text-gray-100 focus:border-blue-500 focus:ring-blue-500 sm:text-sm"
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-400 mb-1">Max Concurrent Runs</label>
                    <input
                      type="number"
                      required
                      min={1}
                      value={localLimits.maxConcurrentRuns}
                      onChange={(e) => setLocalLimits({ ...localLimits, maxConcurrentRuns: parseInt(e.target.value) })}
                      className="block w-full rounded-md border-gray-700 bg-gray-800 text-gray-100 focus:border-blue-500 focus:ring-blue-500 sm:text-sm"
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-400 mb-1">Max Runs per User</label>
                    <input
                      type="number"
                      required
                      min={1}
                      value={localLimits.maxRunsPerUser}
                      onChange={(e) => setLocalLimits({ ...localLimits, maxRunsPerUser: parseInt(e.target.value) })}
                      className="block w-full rounded-md border-gray-700 bg-gray-800 text-gray-100 focus:border-blue-500 focus:ring-blue-500 sm:text-sm"
                    />
                  </div>
                </div>

                <div className="flex gap-3 pt-4 border-t border-gray-800">
                  <button
                    type="submit"
                    disabled={isUpdatingLimits}
                    className="flex-1 flex justify-center items-center gap-2 py-2 px-4 border border-transparent rounded-md shadow-sm text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-blue-500 focus:ring-offset-[#1C2127]"
                  >
                    <Save className="w-4 h-4" />
                    {isUpdatingLimits ? 'Saving...' : 'Save Limits'}
                  </button>
                  <button
                    type="button"
                    onClick={() => {
                      setShowLimitsForm(false)
                      if (limits) {
                        setLocalLimits({
                          maxOrdersPerMinute: limits.maxOrdersPerMinute,
                          maxDailyLoss: limits.maxDailyLoss,
                          maxConcurrentRuns: limits.maxConcurrentRuns,
                          maxRunsPerUser: limits.maxRunsPerUser
                        })
                      }
                    }}
                    className="flex justify-center items-center py-2 px-4 border border-gray-700 rounded-md shadow-sm text-sm font-medium text-gray-300 hover:bg-gray-800 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-gray-500 focus:ring-offset-[#1C2127]"
                  >
                    Cancel
                  </button>
                </div>
              </form>
            ) : limits ? (
              <div className="space-y-6">
                <div className="grid grid-cols-2 gap-y-6 gap-x-4">
                  <div>
                    <p className="text-sm font-medium text-gray-400">Max Orders / Min</p>
                    <p className="mt-1 text-2xl font-semibold text-gray-100">{limits.maxOrdersPerMinute}</p>
                  </div>
                  <div>
                    <p className="text-sm font-medium text-gray-400">Max Daily Loss</p>
                    <p className="mt-1 text-2xl font-semibold text-red-400">₹{limits.maxDailyLoss.toLocaleString()}</p>
                  </div>
                  <div>
                    <p className="text-sm font-medium text-gray-400">Max Concurrent Runs</p>
                    <p className="mt-1 text-2xl font-semibold text-gray-100">{limits.maxConcurrentRuns}</p>
                  </div>
                  <div>
                    <p className="text-sm font-medium text-gray-400">Max Runs / User</p>
                    <p className="mt-1 text-2xl font-semibold text-gray-100">{limits.maxRunsPerUser}</p>
                  </div>
                </div>

                <div className="pt-6 border-t border-gray-800 text-xs text-gray-500 flex justify-between">
                  <span>Source: <span className="font-medium text-gray-400 uppercase">{limits.source}</span></span>
                  {limits.updatedBy && (
                    <span>Last updated by <span className="font-medium text-gray-400">{limits.updatedBy}</span></span>
                  )}
                </div>
              </div>
            ) : (
              <div className="animate-pulse flex space-x-4">
                <div className="flex-1 space-y-4 py-1">
                  <div className="h-4 bg-gray-800 rounded w-3/4"></div>
                  <div className="space-y-2">
                    <div className="h-4 bg-gray-800 rounded"></div>
                    <div className="h-4 bg-gray-800 rounded w-5/6"></div>
                  </div>
                </div>
              </div>
            )}
          </div>
        </div>
      </div>

      {/* RISK EVENTS TABLE */}
      <div className="bg-[#1C2127] border border-gray-800 rounded-lg overflow-hidden">
        <div className="px-6 py-4 border-b border-gray-800 bg-black/20 flex justify-between items-center">
          <h2 className="text-lg font-medium text-gray-100 flex items-center gap-2">
            <Activity className="w-5 h-5 text-gray-400" />
            Recent Risk Events
          </h2>
        </div>
        
        <div className="overflow-x-auto">
          <table className="min-w-full divide-y divide-gray-800">
            <thead className="bg-black/40">
              <tr>
                <th className="px-6 py-3 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">Time</th>
                <th className="px-6 py-3 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">Event</th>
                <th className="px-6 py-3 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">Symbol / Run</th>
                <th className="px-6 py-3 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">Actor</th>
                <th className="px-6 py-3 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">Reason</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-800">
              {events?.map((ev) => (
                <tr key={ev.id} className="hover:bg-gray-800/50 transition-colors">
                  <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-300">
                    <div className="flex items-center gap-1.5">
                      <Clock className="w-3.5 h-3.5 text-gray-500" />
                      {new Date(ev.occurredUtc).toLocaleTimeString()}
                    </div>
                  </td>
                  <td className="px-6 py-4 whitespace-nowrap">
                    <span className={classNames(
                      "inline-flex items-center px-2 py-0.5 rounded text-xs font-medium",
                      ev.kind.includes('KillSwitch') ? "bg-red-500/10 text-red-400" :
                      ev.kind === 'OrderRejected' ? "bg-orange-500/10 text-orange-400" :
                      "bg-blue-500/10 text-blue-400"
                    )}>
                      {ev.kind}
                    </span>
                  </td>
                  <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-300">
                    {ev.symbol && <span className="font-mono text-gray-400 mr-2">{ev.symbol}</span>}
                    {ev.simulationRunId && <span className="text-gray-500">Run #{ev.simulationRunId}</span>}
                    {!ev.symbol && !ev.simulationRunId && <span className="text-gray-600">—</span>}
                  </td>
                  <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-300">
                    <div className="flex items-center gap-1.5">
                      <UserIcon className="w-3.5 h-3.5 text-gray-500" />
                      {ev.actorName || 'system'}
                    </div>
                  </td>
                  <td className="px-6 py-4 text-sm text-gray-400 max-w-xs truncate" title={ev.reason || ''}>
                    {ev.reason || '—'}
                  </td>
                </tr>
              ))}
              {(!events || events.length === 0) && (
                <tr>
                  <td colSpan={5} className="px-6 py-8 text-center text-sm text-gray-500">
                    No recent risk events.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  )
}
