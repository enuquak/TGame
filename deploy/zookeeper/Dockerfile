ARG JAVA_IMAGE=eclipse-temurin:17-jre-jammy
FROM ${JAVA_IMAGE}
RUN apt-get update && apt-get install -y --no-install-recommends netcat-openbsd \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 10001 zookeeper \
    && useradd --uid 10001 --gid 10001 --no-create-home zookeeper
COPY apache-zookeeper-3.8.8-bin.tar.gz /tmp/zookeeper.tar.gz
RUN echo 'c8fea10468ce7f590bcf7e961ae20f4e969ddbc938cd9c32e48d2bc1e82b95e9d9186007f03877726584daf1655f93a23c23b501bab95c8e03d11324135dbc34  /tmp/zookeeper.tar.gz' | sha512sum -c - \
    && mkdir -p /opt/zookeeper /var/lib/zookeeper/data /var/lib/zookeeper/log \
    && tar xzf /tmp/zookeeper.tar.gz --strip-components=1 -C /opt/zookeeper \
    && rm /tmp/zookeeper.tar.gz \
    && chown -R 10001:10001 /opt/zookeeper /var/lib/zookeeper
USER 10001:10001
WORKDIR /opt/zookeeper
ENV ZOO_LOG_DIR=/var/lib/zookeeper/log
EXPOSE 2181
CMD ["/opt/zookeeper/bin/zkServer.sh", "start-foreground"]
