# Section 8/18 of the proposal specifies Asterisk 22 LTS.
#
# NOTE: an earlier version of this Dockerfile tried `apt-get install asterisk` against Debian's
# own repo. That does NOT work - Debian dropped the `asterisk` package years ago (it is absent
# from bullseye/bookworm entirely), so `apt-get install asterisk` always fails with "Package
# 'asterisk' has no installation candidate", no matter how fresh `apt-get update` is.
#
# This build compiles Asterisk 22 from upstream source instead, which is the only reliable path
# on current Debian, and bundles pjproject, because this template's PJSIP config
# (infra/asterisk/conf/pjsip.conf) needs chan_pjsip/res_pjsip*, and Debian's own pjproject-dev
# package is too old for current Asterisk.
#
# This is deliberately kept as ONE stage (not a slim multi-stage copy) - Asterisk's `make
# install` scatters shared libraries (libasteriskssl.so etc.) across several system directories,
# and hand-picking which files a multi-stage COPY needs is a common source of "works to compile,
# fails to start" bugs. Keeping the build toolchain in the final image costs disk space (image
# ends up a few hundred MB larger) but this is a local dev stack, not a production image -
# correctness over size here. Trim it into a multi-stage build later once you know exactly
# which .so files your build produces.
#
# Expect the FIRST `docker compose up --build` to take roughly 10-20 minutes just for this
# container (compiling Asterisk from source) - that is normal, not a hang. Subsequent builds
# reuse Docker's layer cache and are fast unless you change this Dockerfile.

FROM debian:bookworm-slim

RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential wget ca-certificates git subversion pkg-config \
        libnewt-dev libssl-dev libncurses-dev libxml2-dev libxml2-utils \
        uuid-dev libsqlite3-dev libjansson-dev libedit-dev \
        libsrtp2-dev unixodbc-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /usr/src
# The tarball extracts to a version-specific folder (e.g. asterisk-22.10.1/, not
# asterisk-22-current/) - renaming it to a fixed path keeps the WORKDIR below stable across
# point releases instead of hardcoding a version number that will go stale.
RUN wget -q https://downloads.asterisk.org/pub/telephony/asterisk/asterisk-22-current.tar.gz \
    && tar xzf asterisk-22-current.tar.gz \
    && rm asterisk-22-current.tar.gz \
    && mv asterisk-22*/ asterisk-src

WORKDIR /usr/src/asterisk-src

# --with-jansson-bundled / --with-pjproject-bundled: build these two deps from the vendored
# copies in the Asterisk tarball instead of requiring matching -dev packages from the distro,
# since Debian's versions are usually too old for current Asterisk.
RUN ./configure --with-jansson-bundled --with-pjproject-bundled

# Default menuselect module set already enables what pjsip.conf/ari.conf/http.conf need:
# chan_pjsip, res_pjsip*, res_ari*, res_stasis, res_http_websocket, app_dial, etc.
RUN make -j"$(nproc)" \
    && make install \
    && make samples \
    && make config \
    && ldconfig

# Bundle the WAV core prompt pack so ARI welcome playback always has media.
RUN mkdir -p /var/lib/asterisk/sounds/en \
    && wget -qO /tmp/asterisk-core-sounds-en-wav.tar.gz \
       https://downloads.asterisk.org/pub/telephony/sounds/asterisk-core-sounds-en-wav-current.tar.gz \
    && tar xzf /tmp/asterisk-core-sounds-en-wav.tar.gz -C /var/lib/asterisk/sounds/en \
    && rm /tmp/asterisk-core-sounds-en-wav.tar.gz

# infra/asterisk/conf/asterisk.conf sets `runuser = asterisk` / `rungroup = asterisk`, but a
# from-source build (unlike the Debian `asterisk` package, which no longer exists - see the top
# of this file) never creates that system user/group on its own. Without this, Asterisk exits
# immediately on startup with "No such group 'asterisk'!". Create it and hand it ownership of
# the directories it needs to write to.
RUN groupadd --system asterisk \
    && useradd --system --gid asterisk --home-dir /var/lib/asterisk --shell /usr/sbin/nologin asterisk

# Config is bind-mounted from ./conf in docker-compose.yml so you can edit it without rebuilding.
RUN mkdir -p /var/log/asterisk /var/spool/asterisk /var/run/asterisk \
    && chown -R asterisk:asterisk /var/log/asterisk /var/spool/asterisk /var/run/asterisk \
        /var/lib/asterisk /usr/lib/asterisk

EXPOSE 5060/udp 5060/tcp 8088/tcp 8089/tcp 5038/tcp
EXPOSE 10000-10100/udp

CMD ["asterisk", "-f", "-vvv"]
