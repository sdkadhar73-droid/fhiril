# Assessment-only image build. The upstream eCRNow Dockerfile remains unchanged.
# Source files are provided as the named ecrnow-source build context by Compose.
FROM maven:3.9.9-eclipse-temurin-17 AS build
WORKDIR /workspace
COPY --from=ecrnow-source pom.xml ./pom.xml
COPY --from=ecrnow-source src ./src
RUN mvn --batch-mode clean package -Dmaven.test.skip=true

FROM eclipse-temurin:17-jre
WORKDIR /app
COPY --from=build /workspace/target/ecr-now.war /app/ecr-now.war
EXPOSE 8080
ENTRYPOINT ["java", "-jar", "/app/ecr-now.war"]
