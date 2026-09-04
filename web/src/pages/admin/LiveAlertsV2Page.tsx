import { useState } from 'react'
import { Bell, PlayCircle, AlertTriangle, ShieldAlert, CheckCircle2, Info } from 'lucide-react'
import { useAlertEvents } from '../../lib/queries'
import { api } from '../../lib/api'

function classNames(...classes: (string | undefined | null | false)[]) {
  return classes.filter(Boolean).join(' ')
}

export function LiveAlertsV2Page() {
  const { data: events, refetch } = useAlertEvents(100)
  const [isTesting, setIsTesting] = useState(false)
  const [testResult, setTestResult] = useState<{ type: 'success' | 'error'; message: string } | null>(null)

  const handleTestAlert = async (instrument: string) => {
    try {
      setIsTesting(true)
      setTestResult(null)
      const res = await api.post<{ status: string; message: string }>('/api/Alerts/test-e2e', {
        instrument
      })
      setTestResult({ type: 'success', message: res.message })
      // Give the backend a second to process the redis message and write to DB
      setTimeout(() => refetch(), 1500)
    } catch (err: any) {
      setTestResult({ 
        type: 'error', 
        message: err?.body?.message || err.message || 'Failed to trigger test alert' 
      })
    } finally {
      setIsTesting(false)
    }
  }

  const getSeverityIcon = (severity: string) => {
    switch (severity?.toLowerCase()) {
      case 'error':
      case 'critical':
        return <ShieldAlert className="w-4 h-4 text-red-400" />
      case 'warning':
        return <AlertTriangle className="w-4 h-4 text-orange-400" />
      case 'success':
        return <CheckCircle2 className="w-4 h-4 text-green-400" />
      default:
        return <Info className="w-4 h-4 text-blue-400" />
    }
  }

  const getSeverityClass = (severity: string) => {
    switch (severity?.toLowerCase()) {
      case 'error':
      case 'critical':
        return 'bg-red-500/10 text-red-400 border border-red-500/20'
      case 'warning':
        return 'bg-orange-500/10 text-orange-400 border border-orange-500/20'
      case 'success':
        return 'bg-green-500/10 text-green-400 border border-green-500/20'
      default:
        return 'bg-blue-500/10 text-blue-400 border border-blue-500/20'
    }
  }

  return (
    <div className="space-y-6 max-w-7xl mx-auto p-4 sm:p-6 lg:p-8">
      <div>
        <h1 className="text-2xl font-bold text-gray-100 flex items-center gap-2">
          <Bell className="w-6 h-6 text-blue-500" />
          Live Alerts Console
        </h1>
        <p className="mt-1 text-sm text-gray-400">
          Real-time stream of system and strategy alerts, synchronized with Telegram dispatches.
        </p>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-4 gap-6">
        {/* Test Controls */}
        <div className="lg:col-span-1 space-y-6">
          <div className="bg-[#1C2127] border border-gray-800 rounded-lg overflow-hidden">
            <div className="px-6 py-4 border-b border-gray-800 bg-black/20">
              <h2 className="text-lg font-medium text-gray-100 flex items-center gap-2">
                <PlayCircle className="w-5 h-5 text-gray-400" />
                E2E Diagnostics
              </h2>
            </div>
            <div className="p-6 space-y-4">
              <p className="text-sm text-gray-400">
                Trigger a mock signal to verify the complete pipeline: Python LogicEngine → Redis Pub/Sub → API Background Service → Telegram API & PostgreSQL.
              </p>
              
              <div className="space-y-3">
                <button
                  onClick={() => handleTestAlert('BANKNIFTY')}
                  disabled={isTesting}
                  className="w-full flex justify-center items-center py-2 px-4 border border-gray-700 rounded-md shadow-sm text-sm font-medium text-gray-300 bg-gray-800 hover:bg-gray-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-blue-500 focus:ring-offset-[#1C2127] disabled:opacity-50"
                >
                  Test BankNifty Alert
                </button>
                <button
                  onClick={() => handleTestAlert('RELIANCE')}
                  disabled={isTesting}
                  className="w-full flex justify-center items-center py-2 px-4 border border-gray-700 rounded-md shadow-sm text-sm font-medium text-gray-300 bg-gray-800 hover:bg-gray-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-blue-500 focus:ring-offset-[#1C2127] disabled:opacity-50"
                >
                  Test Reliance Alert
                </button>
              </div>

              {testResult && (
                <div className={classNames(
                  "p-3 rounded-md text-sm border",
                  testResult.type === 'success' 
                    ? "bg-emerald-500/10 text-emerald-400 border-emerald-500/20" 
                    : "bg-red-500/10 text-red-400 border-red-500/20"
                )}>
                  {testResult.message}
                </div>
              )}
            </div>
          </div>
        </div>

        {/* Alerts Stream */}
        <div className="lg:col-span-3">
          <div className="bg-[#1C2127] border border-gray-800 rounded-lg overflow-hidden flex flex-col h-[calc(100vh-12rem)] min-h-[500px]">
            <div className="px-6 py-4 border-b border-gray-800 bg-black/20 flex justify-between items-center shrink-0">
              <h2 className="text-lg font-medium text-gray-100 flex items-center gap-2">
                <Bell className="w-5 h-5 text-gray-400" />
                Alerts Stream
              </h2>
              <span className="text-xs text-gray-500">
                Showing last {events?.length || 0} events
              </span>
            </div>
            
            <div className="overflow-y-auto flex-1">
              {events && events.length > 0 ? (
                <div className="divide-y divide-gray-800">
                  {events.map((ev) => (
                    <div key={ev.id} className="p-4 sm:px-6 hover:bg-gray-800/30 transition-colors">
                      <div className="flex items-start justify-between gap-4">
                        <div className="flex-1 space-y-1">
                          <div className="flex items-center gap-3">
                            <span className={classNames(
                              "inline-flex items-center gap-1.5 px-2.5 py-0.5 rounded-full text-xs font-medium",
                              getSeverityClass(ev.severity)
                            )}>
                              {getSeverityIcon(ev.severity)}
                              {ev.severity.toUpperCase()}
                            </span>
                            <span className="text-sm font-medium text-gray-200">
                              {ev.title}
                            </span>
                            {ev.underlying && (
                              <span className="text-xs font-mono text-gray-500">
                                [{ev.underlying}]
                              </span>
                            )}
                          </div>
                          <p className="text-sm text-gray-400 break-words whitespace-pre-line">
                            {ev.message}
                          </p>
                          <div className="flex items-center gap-4 text-xs text-gray-500 pt-2">
                            <span>{new Date(ev.occurredUtc).toLocaleString()}</span>
                            <span>Source: <span className="font-medium text-gray-400">{ev.source}</span></span>
                            {ev.symbol && <span>Symbol: <span className="font-mono text-gray-400">{ev.symbol}</span></span>}
                            {ev.simulationRunId && <span>Run: <span className="text-gray-400">#{ev.simulationRunId}</span></span>}
                          </div>
                        </div>
                        <div className="shrink-0 flex items-center">
                          {ev.deliveredToTelegram ? (
                            <span className="flex items-center gap-1 text-xs font-medium text-emerald-400 bg-emerald-400/10 px-2 py-1 rounded">
                              <CheckCircle2 className="w-3.5 h-3.5" />
                              Telegram
                            </span>
                          ) : (
                            <span className="flex items-center gap-1 text-xs font-medium text-gray-500 bg-gray-800 px-2 py-1 rounded">
                              Local Only
                            </span>
                          )}
                        </div>
                      </div>
                    </div>
                  ))}
                </div>
              ) : (
                <div className="h-full flex flex-col items-center justify-center text-gray-500 p-8 space-y-3">
                  <Bell className="w-12 h-12 text-gray-800" />
                  <p>No alerts in the current stream.</p>
                </div>
              )}
            </div>
          </div>
        </div>
      </div>
    </div>
  )
}
