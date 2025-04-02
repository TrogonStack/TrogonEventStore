
FROM ubuntu:20.04

ARG TARGET_IMAGE

# Install necessary packages
RUN apt-get update && \
  apt-get install -y \
  curl \
  git \
  jq \
  act \
  && rm -rf /var/lib/apt/lists/*
# Install Node.js (required for many actions)
RUN curl -fsSL https://deb.nodesource.com/setup_20.x | bash - && \
  apt-get install -y nodejs

# Install GitHub CLI
RUN curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg | dd of=/usr/share/keyrings/githubcli-archive-keyring.gpg && \
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" | tee /etc/apt/sources.list.d/github-cli.list > /dev/null && \
  apt update && \
  apt install gh -y

# Copy your workflow files into the container
#COPY .github/workflows/* /home/runner/*

WORKDIR /home/runner
COPY . .


# Set up a non-root user
RUN useradd -ms /bin/bash runner
USER runner


# Run the workflow
# CMD ["bash", "-c", "gh run download --repo rgfaber/TrogonEventStore --name build-container-alpine && bash ${TARGET_IMAGE}.yml"]
CMD ["bash", "-c", "act -j test"]
