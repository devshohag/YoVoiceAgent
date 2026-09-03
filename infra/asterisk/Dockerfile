# Asterisk 22 source build for PJSIP, ARI, Stasis and RTP support.
FROM debian:bookworm-slim

ARG DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        build-essential \
        ca-certificates \
        git \
        libedit-dev \
        libjansson-dev \
        libncurses-dev \
        libnewt-dev \
        libsrtp2-dev \
        libsqlite3-dev \
        libssl-dev \
        libxml2-dev \
        libxml2-utils \
        pkg-config \
        subversion \
        unixodbc-dev \
        uuid-dev \
        wget \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /usr/src

RUN wget --quiet --https-only \
        https://downloads.asterisk.org/pub/telephony/asterisk/asterisk-22-current.tar.gz \
        --output-document asterisk.tar.gz \
    && tar --extract --gzip --file asterisk.tar.gz \
    && rm asterisk.tar.gz \
    && mv asterisk-22*/ asterisk-src

WORKDIR /usr/src/asterisk-src

RUN ./configure \
        --with-jansson-bundled \
        --with-pjproject-bundled

# The bundled pjproject launches nested make processes.
# Serial compilation prevents the libpjmath/libpjsip archive race
# observed on GitHub-hosted runners.
ENV MAKEFLAGS="-j1"

RUN make -j1 \
    && make install \
    && make samples \
    && make config \
    && ldconfig

RUN groupadd --system asterisk \
    && useradd \
        --system \
        --gid asterisk \
        --home-dir /var/lib/asterisk \
        --shell /usr/sbin/nologin \
        asterisk

RUN mkdir -p \
        /var/lib/asterisk \
        /var/log/asterisk \
        /var/run/asterisk \
        /var/spool/asterisk/recording \
    && chown -R asterisk:asterisk \
        /var/lib/asterisk \
        /var/log/asterisk \
        /var/run/asterisk \
        /var/spool/asterisk \
        /usr/lib/asterisk

EXPOSE 5060/udp
EXPOSE 5060/tcp
EXPOSE 5038/tcp
EXPOSE 8088/tcp
EXPOSE 8089/tcp
EXPOSE 10000-10100/udp

STOPSIGNAL SIGTERM

CMD ["asterisk", "-f", "-vvv"]