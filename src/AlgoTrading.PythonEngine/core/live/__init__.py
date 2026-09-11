"""
The vendor-neutral half of a live feed.

A streamer is two things: the part that knows a vendor's socket and message
shape, and the part that knows this platform — its watchlist, its tick endpoint,
its heartbeat, and what to do when prices stop arriving. Only the first half is
worth writing twice.
"""
