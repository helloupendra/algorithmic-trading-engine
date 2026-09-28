# The Dhan feed load test's Linux box (tools/feed_loadtest/matrix.sh builds it).
# Python and the feed's libraries pinned to what production runs; redis-server,
# socat (the docker-proxy stand-in), procps and openssl (a throwaway cert made
# at run time; nothing is committed).
FROM python:3.12-slim
RUN apt-get update \
 && apt-get install -y --no-install-recommends redis-server redis-tools socat procps openssl \
 && rm -rf /var/lib/apt/lists/*
RUN pip install --no-cache-dir websocket-client==1.6.1 redis==8.1.0 requests==2.31.0 simplejson==3.20.2 \
    py_vollib==1.0.12 vollib==1.0.11 lets_be_rational==1.1.2 python-dotenv==1.2.3 numpy scipy pydantic \
    prometheus_client
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
WORKDIR /tmp
