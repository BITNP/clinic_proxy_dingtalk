FROM node:18-slim AS build-frontend
RUN npm config set registry https://registry.npmmirror.com/
COPY clinic_wechat/records /build
WORKDIR /build
RUN npm install && \
    npm run build

from mcr.microsoft.com/dotnet/sdk:8.0 AS build-proxy
COPY . /build
WORKDIR /build
RUN dotnet publish -r linux-x64 -c Release

FROM debian:bookworm-slim
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
ENV ASPNETCORE_URLS=http://0.0.0.0:80
COPY --from=build-proxy /build/bin/Release/net8.0/linux-x64/publish /app
COPY --from=build-frontend /build/dist /app/wwwroot
WORKDIR /app
EXPOSE 80
CMD ["/app/clinic_proxy_dingtalk"]
