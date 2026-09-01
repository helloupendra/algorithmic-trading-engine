import React, { useState, useEffect, useRef } from 'react';
import { Panel, Badge } from '../../components/ui';
import { api } from '../../lib/api';
import { useAlerterStatus, useStartAlerter, useStopAlerter, useAlerterLogs, useLatestQuotes } from '../../lib/queries';
import { formatAge } from '../../lib/format';

interface E2EResponse {
    status?: string;
    message?: string;
    broadcastedPayload?: any;
    [key: string]: any;
}

export function LiveAlertsPage() {
    const statusQuery = useAlerterStatus();
    const logsQuery = useAlerterLogs();
    const startAlerter = useStartAlerter();
    const stopAlerter = useStopAlerter();

    const daemonLogsContainerRef = useRef<HTMLDivElement>(null);

    useEffect(() => {
        if (daemonLogsContainerRef.current) {
            const container = daemonLogsContainerRef.current;
            container.scrollTop = container.scrollHeight;
        }
    }, [logsQuery.data]);

    const [instrument, setInstrument] = useState<string>('BSE:SENSEX-INDEX');
    const [loading, setLoading] = useState<boolean>(false);
    const [logs, setLogs] = useState<{time: string, msg: string, type: 'info'|'success'|'error'}[]>([]);

    const appendLog = (msg: string, type: 'info'|'success'|'error' = 'info') => {
        setLogs(prev => [...prev, { time: new Date().toLocaleTimeString(), msg, type }]);
    };

    const handleTestAlert = async () => {
        setLoading(true);
        appendLog(`Broadcasting E2E command for ${instrument} to .NET Backend...`, 'info');
        
        try {
            const data = await api.post<E2EResponse>('/api/alerts/test-e2e', { instrument });
            
            if (data.status === 'success') {
                appendLog(`Successfully received 200 OK from .NET API.`, 'success');
                appendLog(`Response: ${JSON.stringify(data, null, 2)}`, 'success');
            } else {
                appendLog(`Error from API: ${data.message || JSON.stringify(data)}`, 'error');
            }
        } catch (error: any) {
            appendLog(`Fetch exception: ${error.message || error}`, 'error');
        }
        
        setLoading(false);
    };

    const quotesQuery = useLatestQuotes();
    const coreIndices = ['NSE:NIFTY50-INDEX', 'NSE:NIFTYBANK-INDEX', 'BSE:SENSEX-INDEX'];

    return (
        <div className="page">
            <header className="page__header">
                <h1 className="page__title flex items-center gap-4">
                    Live Alerts
                    {statusQuery.data?.isRunning ? (
                        <Badge tone="pos">Running</Badge>
                    ) : (
                        <Badge tone="neutral">Stopped</Badge>
                    )}
                </h1>
                <p className="page__subtitle">
                    End-to-End simulation and trigger for real-time Telegram signals.
                </p>
                {statusQuery.data?.isRunning && statusQuery.data?.startedUtc && (
                    <p className="text-xs text-gray-400 mt-2">
                        Daemon started {formatAge(statusQuery.data.startedUtc)} ago
                    </p>
                )}
            </header>

            <div className="mb-6 flex flex-col gap-px bg-gray-800/50 rounded-md overflow-hidden border border-gray-800">
                {coreIndices.map(symbol => {
                    const quote = quotesQuery.data?.find(q => q.symbol === symbol);
                    return (
                        <div key={symbol} className="flex">
                            <div className="w-1/2 p-3 bg-[#111] border-r border-gray-800 flex items-center">
                                <span className="font-mono text-sm text-gray-200">{symbol.split(':')[1]}</span>
                            </div>
                            <div className="w-1/2 p-3 bg-[#3d1a1a] flex items-center justify-end">
                                <span className="font-mono text-sm font-semibold text-gray-100">
                                    {quote?.lastTradedPrice ? quote.lastTradedPrice.toLocaleString('en-IN', { minimumFractionDigits: 2 }) : '---'}
                                </span>
                            </div>
                        </div>
                    );
                })}
            </div>

            <Panel title="Daemon Controls" className="mb-6">
                <div className="flex items-center gap-4">
                    {!statusQuery.data?.isRunning ? (
                        <button 
                            onClick={() => startAlerter.mutate()}
                            disabled={startAlerter.isPending}
                            className="bg-green-600 hover:bg-green-700 disabled:opacity-50 text-white font-medium text-sm px-4 py-1.5 rounded transition-colors"
                        >
                            {startAlerter.isPending ? 'Starting...' : 'Start Daemon'}
                        </button>
                    ) : (
                        <button 
                            onClick={() => stopAlerter.mutate()}
                            disabled={stopAlerter.isPending}
                            className="bg-red-600 hover:bg-red-700 disabled:opacity-50 text-white font-medium text-sm px-4 py-1.5 rounded transition-colors"
                        >
                            {stopAlerter.isPending ? 'Stopping...' : 'Stop Daemon'}
                        </button>
                    )}
                    <span className="text-sm text-gray-400">
                        Starts the background python script to monitor the engine and send live alerts to Telegram.
                    </span>
                </div>

                {/* Daemon Terminal Console */}
                <div className="bg-black border border-gray-800 rounded-md flex flex-col overflow-hidden shadow-inner mt-4">
                    <div className="flex items-center bg-[#111] px-3 py-2 border-b border-gray-800">
                        <div className="w-2.5 h-2.5 rounded-full bg-red-500 mr-2"></div>
                        <div className="w-2.5 h-2.5 rounded-full bg-yellow-500 mr-2"></div>
                        <div className="w-2.5 h-2.5 rounded-full bg-green-500"></div>
                        <span className="ml-3 text-xs text-gray-500 uppercase tracking-wider font-mono">Python Daemon Console</span>
                    </div>
                    
                    <div ref={daemonLogsContainerRef} className="p-3 h-64 overflow-y-auto font-mono text-xs leading-relaxed text-gray-400">
                        {!logsQuery.data || logsQuery.data.length === 0 ? (
                            <span className="text-gray-600 italic">No output from daemon...</span>
                        ) : (
                            logsQuery.data.map((log, i) => (
                                <div key={i} className="whitespace-pre-wrap">{log}</div>
                            ))
                        )}
                    </div>
                </div>
            </Panel>

            <Panel title="E2E Alert Trigger">
                {/* Inline Controls Row */}
                <div className="flex items-center gap-4 mb-5">
                    <div className="flex items-center gap-3">
                        <label className="text-sm font-medium text-gray-400 whitespace-nowrap">Target Instrument:</label>
                        <select 
                            value={instrument}
                            onChange={(e) => setInstrument(e.target.value)}
                            className="bg-[#1a1a1a] border border-gray-700 text-sm rounded px-3 py-1.5 text-gray-200 outline-none focus:border-blue-500 min-w-[140px]"
                        >
                            <option value="BSE:SENSEX-INDEX">SENSEX</option>
                            <option value="NSE:NIFTY50-INDEX">NIFTY50</option>
                            <option value="NSE:BANKNIFTY-INDEX">BANKNIFTY</option>
                            <option value="NSE:HDFCBANK-EQ">HDFCBANK</option>
                            <option value="NSE:RELIANCE-EQ">RELIANCE</option>
                        </select>
                    </div>

                    <button 
                        onClick={handleTestAlert}
                        disabled={loading}
                        className="bg-blue-600 hover:bg-blue-700 disabled:opacity-50 text-white font-medium text-sm px-4 py-1.5 rounded transition-colors flex items-center"
                    >
                        {loading ? 'Sending...' : 'Trigger Alert'}
                    </button>
                </div>

                {/* Terminal Console */}
                <div className="bg-black border border-gray-800 rounded-md flex flex-col overflow-hidden shadow-inner">
                    <div className="flex items-center bg-[#111] px-3 py-2 border-b border-gray-800">
                        <div className="w-2.5 h-2.5 rounded-full bg-red-500 mr-2"></div>
                        <div className="w-2.5 h-2.5 rounded-full bg-yellow-500 mr-2"></div>
                        <div className="w-2.5 h-2.5 rounded-full bg-green-500"></div>
                        <span className="ml-3 text-xs text-gray-500 uppercase tracking-wider font-mono">Terminal Session</span>
                    </div>
                    
                    <div className="p-3 h-48 overflow-y-auto font-mono text-xs leading-relaxed">
                        {logs.length === 0 && (
                            <span className="text-gray-600 italic">Waiting for command execution...</span>
                        )}
                        {logs.map((log, i) => (
                            <div key={i} className={`mb-1 ${log.type === 'error' ? 'text-red-400' : log.type === 'success' ? 'text-green-400' : 'text-gray-400'}`}>
                                <span className="text-gray-600 mr-2">[{log.time}]</span>
                                <span className="whitespace-pre-wrap">{log.msg}</span>
                            </div>
                        ))}
                    </div>
                </div>
            </Panel>
        </div>
    );
}
