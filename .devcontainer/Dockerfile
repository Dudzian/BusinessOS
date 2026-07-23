FROM mcr.microsoft.com/dotnet/sdk:10.0.100
ARG POWERSHELL_VERSION=7.4.6
ARG POWERSHELL_SHA256=6F6015203C47806C5CC444C19D8ED019695E610FBD948154264BF9CA8E157561
RUN apt-get update \
  && apt-get install -y --no-install-recommends curl ca-certificates git ripgrep tar gzip \
  && rm -rf /var/lib/apt/lists/*
RUN curl -fsSL -o /tmp/powershell.tar.gz "https://github.com/PowerShell/PowerShell/releases/download/v${POWERSHELL_VERSION}/powershell-${POWERSHELL_VERSION}-linux-x64.tar.gz" \
  && echo "${POWERSHELL_SHA256}  /tmp/powershell.tar.gz" | sha256sum -c - \
  && mkdir -p /opt/microsoft/powershell/7 \
  && tar -xzf /tmp/powershell.tar.gz -C /opt/microsoft/powershell/7 \
  && chmod +x /opt/microsoft/powershell/7/pwsh \
  && ln -s /opt/microsoft/powershell/7/pwsh /usr/local/bin/pwsh \
  && rm /tmp/powershell.tar.gz
RUN test "$(dotnet --version)" = "10.0.100"
RUN test "$(pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()')" = "7.4.6"
ENV DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1
