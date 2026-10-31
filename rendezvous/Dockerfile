FROM golang:1.22-alpine AS build
WORKDIR /src
COPY go.mod *.go ./
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /rendezvous .

FROM gcr.io/distroless/static-debian12:nonroot
COPY --from=build /rendezvous /rendezvous
USER nonroot
EXPOSE 47900
ENTRYPOINT ["/rendezvous"]
