FROM scratch
# 打包时写入完整运行环境，飞牛安装时不需要拉取基础镜像。
ADD rootfs.tar /
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true
WORKDIR /app
ENTRYPOINT ["./FujiAutoSave"]
