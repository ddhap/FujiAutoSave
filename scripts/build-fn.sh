#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUTPUT="${FPK_OUTPUT_DIR:-${ROOT}/artifacts}"
PACKAGE="${OUTPUT}/fn-package"
VERSION="$(awk -F= '$1 == "version" {print $2}' "${ROOT}/src/fn/manifest")"
IMAGE="${FUJI_FPK_IMAGE:-fuji-autosave:fpk-${VERSION}}"

mkdir -p "${PACKAGE}" "${OUTPUT}"
cp -R "${ROOT}/src/fn/." "${PACKAGE}/"

# 可指定已构建的修复镜像；默认从当前源码完整构建。
if [ -z "${FUJI_FPK_IMAGE:-}" ]; then
    docker build --platform linux/amd64 -t "${IMAGE}" "${ROOT}"
fi

ARCH="$(docker image inspect --format '{{.Os}}/{{.Architecture}}' "${IMAGE}")"
if [ "${ARCH}" != "linux/amd64" ]; then
    echo "需要 linux/amd64 镜像，当前为 ${ARCH}" >&2
    exit 1
fi

# 仅导出新建且从未启动的容器，不包含用户照片或运行时配置。
CONTAINER="$(docker create --platform linux/amd64 "${IMAGE}")"
trap 'docker rm "${CONTAINER}" >/dev/null' EXIT
docker export -o "${PACKAGE}/app/docker/rootfs.tar" "${CONTAINER}"
chmod +x "${PACKAGE}/cmd/"*

docker run --rm --platform linux/amd64 \
    -v "${ROOT}/tools/fnpack:/tools/fnpack:ro" \
    -v "${PACKAGE}:/package:ro" \
    -v "${OUTPUT}:/output" -w /output \
    --entrypoint /bin/sh "${IMAGE}" -c \
    'cp /tools/fnpack /tmp/fnpack && chmod +x /tmp/fnpack && /tmp/fnpack build -d /package'

echo "安装包输出目录：${OUTPUT}"
